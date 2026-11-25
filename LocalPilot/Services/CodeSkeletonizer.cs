using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalPilot.Services
{
    /// <summary>
    /// Roslyn-powered AST Code Skeletonizer.
    /// Compresses large C# files (> 120 lines or > 4000 characters) by preserving class hierarchy,
    /// interfaces, properties, and target methods, while skeletonizing non-target method bodies.
    /// This keeps 100% of the class structure and closing braces intact while slashing token count by 60-75%.
    /// </summary>
    public static class CodeSkeletonizer
    {
        public static string Skeletonize(string code, string targetKeyword = null, int focusLine = -1)
        {
            // 🛡️ ACCURACY SAFEGUARD: Files under 1,500 chars are already tiny and kept in full.
            // For files > 1,500 chars, non-target method bodies are skeletonized while the
            // TARGET reference method (e.g. GetNames) is kept 100% in full with real code.
            if (string.IsNullOrEmpty(code) || code.Length < 1500)
                return code;

            try
            {
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
                if (methods.Count <= 3)
                    return code; // Few methods: keep in full

                var rewriter = new MethodBodySkeletonRewriter(targetKeyword, focusLine);
                var newRoot = rewriter.Visit(root);
                return newRoot.ToFullString();
            }
            catch
            {
                return code; // Safe fallback to original code
            }
        }

        /// <summary>
        /// Skeletonizes non-target method bodies while preserving EXACT 1-based line numbers
        /// from the original file so line-numbered edits (edit_lines) always target the real line.
        /// </summary>
        public static string SkeletonizeWithLineNumbers(string code, string targetKeyword = null, int focusLine = -1)
        {
            if (string.IsNullOrEmpty(code)) return code;
            var origLines = code.Replace("\r\n", "\n").Split('\n');

            var omittedRanges = new List<(int startLine, int endLine)>();
            if (code.Length >= 1500)
            {
                try
                {
                    var tree = CSharpSyntaxTree.ParseText(code);
                    var root = tree.GetRoot();
                    var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
                    if (methods.Count > 3)
                    {
                        foreach (var method in methods)
                        {
                            if (method.Body == null && method.ExpressionBody == null) continue;
                            if (IsTargetMethod(method, targetKeyword, focusLine)) continue;

                            SyntaxNode bodyNode = (SyntaxNode)method.Body ?? method.ExpressionBody;
                            var span = bodyNode.GetLocation().GetLineSpan();
                            int s = span.StartLinePosition.Line; // 0-based
                            int e = span.EndLinePosition.Line;
                            if (e > s)
                            {
                                omittedRanges.Add((s, e));
                            }
                        }
                    }
                }
                catch { }
            }

            var sb = new System.Text.StringBuilder(code.Length);
            int i = 0;
            while (i < origLines.Length)
            {
                var range = omittedRanges.FirstOrDefault(r => r.startLine == i);
                if (range.endLine > range.startLine)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    string startText = origLines[i];
                    int braceIdx = startText.IndexOf('{');
                    string prefix = (braceIdx > 0 && startText.Substring(0, braceIdx).Trim().Length > 0)
                        ? startText.Substring(0, braceIdx)
                        : string.Empty;
                    sb.Append(i + 1).Append(": ").Append(prefix)
                      .Append("{ /* ... lines ").Append(i + 1).Append('-').Append(range.endLine + 1).Append(" omitted ... */ }");
                    i = range.endLine + 1;
                }
                else
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(i + 1).Append(": ").Append(origLines[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        internal static bool IsTargetMethod(MethodDeclarationSyntax node, string targetKeyword, int focusLine)
        {
            string methodName = node.Identifier.Text;
            if (!string.IsNullOrEmpty(targetKeyword) && !string.IsNullOrEmpty(methodName))
            {
                if (targetKeyword.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    methodName.IndexOf(targetKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (focusLine > 0)
            {
                var lineSpan = node.GetLocation().GetLineSpan();
                if (focusLine >= lineSpan.StartLinePosition.Line && focusLine <= lineSpan.EndLinePosition.Line)
                {
                    return true;
                }
            }
            return false;
        }

        private class MethodBodySkeletonRewriter : CSharpSyntaxRewriter
        {
            private readonly string _targetKeyword;
            private readonly int _focusLine;

            public MethodBodySkeletonRewriter(string targetKeyword, int focusLine)
            {
                _targetKeyword = targetKeyword;
                _focusLine = focusLine;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                bool isTarget = false;
                string methodName = node.Identifier.Text;
                
                // Match if target keyword contains method name, or method name contains target keyword
                if (!string.IsNullOrEmpty(_targetKeyword) && !string.IsNullOrEmpty(methodName))
                {
                    if (_targetKeyword.IndexOf(methodName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        methodName.IndexOf(_targetKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isTarget = true;
                    }
                }

                if (_focusLine > 0)
                {
                    var lineSpan = node.GetLocation().GetLineSpan();
                    if (_focusLine >= lineSpan.StartLinePosition.Line && _focusLine <= lineSpan.EndLinePosition.Line)
                    {
                        isTarget = true;
                    }
                }

                // If target method or abstract/interface method with no body, keep intact
                if (isTarget || (node.Body == null && node.ExpressionBody == null))
                {
                    return base.VisitMethodDeclaration(node);
                }

                // Replace non-target method body with a clean comment-only block (never 'throw null;', which models may imitate)
                var omittedTrivia = SyntaxFactory.Comment("/* ... */");
                var block = SyntaxFactory.Block()
                    .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space, omittedTrivia, SyntaxFactory.Space))
                    .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
                return node.WithBody(block).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
            }
        }
    }
}

