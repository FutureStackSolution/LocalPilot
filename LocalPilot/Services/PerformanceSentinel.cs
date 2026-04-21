using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    public class PerformanceIssue
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public string Severity { get; set; } // "Warning", "Info"
    }

    /// <summary>
    /// The 'Performance Sentinel' - Scans active files for O(n^2) patterns, 
    /// blocking async calls, and high-allocation anti-patterns.
    /// </summary>
    public class PerformanceSentinel
    {
        private static readonly Lazy<PerformanceSentinel> _instance = new Lazy<PerformanceSentinel>(() => new PerformanceSentinel());
        public static PerformanceSentinel Instance => _instance.Value;

        public async Task<List<PerformanceIssue>> AnalyzeFileAsync(string filePath, string sourceCode)
        {
            var issues = new List<PerformanceIssue>();
            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = await tree.GetRootAsync();
                
                var walker = new PerformanceSyntaxWalker();
                walker.Visit(root);
                
                foreach (var match in walker.Matches)
                {
                    issues.Add(new PerformanceIssue
                    {
                        Title = match.Title,
                        Description = match.Description,
                        FilePath = filePath,
                        Line = match.Line,
                        Severity = "Performance Warning"
                    });
                }
            }
            catch { }
            return issues;
        }
    }

    internal class PerformanceSyntaxWalker : CSharpSyntaxWalker
    {
        public List<(string Title, string Description, int Line)> Matches { get; } = new List<(string, string, int)>();

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            // 🚀 O(N^2) DETECTION: Check for nested for/foreach
            if (node.DescendantNodes().OfType<ForEachStatementSyntax>().Any() || 
                node.DescendantNodes().OfType<ForStatementSyntax>().Any())
            {
                Matches.Add((
                    "Nested Loop Detected",
                    "Potential O(n^2) complexity. Consider using a Dictionary or HashSet for O(1) lookups.",
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                ));
            }
            base.VisitForEachStatement(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // 🚀 BLOCKING ASYNC DETECTION: .Result or .Wait()
            var name = node.Name.ToString();
            if (name == "Result" || name == "Wait")
            {
                Matches.Add((
                    "Blocking Async Call",
                    "Using .Result or .Wait() can cause deadlocks and thread-pool starvation. Use await instead.",
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                ));
            }
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            // 🚀 STRING CONCAT IN LOOP DETECTION
            if (node.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken) && 
                node.Left is IdentifierNameSyntax id && 
                (node.Ancestors().OfType<ForEachStatementSyntax>().Any() || node.Ancestors().OfType<ForStatementSyntax>().Any()))
            {
                // Basic heuristic for string concat
                Matches.Add((
                    "String Concatenation in Loop",
                    "High-frequency string allocation detected. Use StringBuilder for better performance.",
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                ));
            }
            base.VisitAssignmentExpression(node);
        }
    }
}
