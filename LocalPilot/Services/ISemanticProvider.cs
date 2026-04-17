using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalPilot.Models;

namespace LocalPilot.Services
{
    /// <summary>
    /// Base interface for language-specific semantic intelligence.
    /// Allows the agent to use Roslyn for C# and LSP/Heuristics for others.
    /// </summary>
    public interface ISemanticProvider
    {
        /// <summary>
        /// Returns true if this provider is the best match for the given file extension.
        /// </summary>
        bool CanHandle(string extension);

        /// <summary>
        /// Returns a summary of the provider's capabilities for the agent prompt.
        /// </summary>
        string GetSummary();

        /// <summary>
        /// Finds definitions for a symbol across the workspace.
        /// </summary>
        Task<List<SymbolLocation>> FindDefinitionsAsync(string symbolName, CancellationToken ct);

        /// <summary>
        /// Provides semantic neighborhood context for a specific file.
        /// </summary>
        Task<string> GetNeighborhoodContextAsync(string filePath, CancellationToken ct);

        /// <summary>
        /// Retrieves project-wide or file-specific diagnostics (errors).
        /// </summary>
        Task<string> GetDiagnosticsAsync(CancellationToken ct);

        /// <summary>
        /// Performs a semantic refactoring (rename) across the solution.
        /// </summary>
        Task<string> RenameSymbolAsync(string filePath, int line, int column, string newName, CancellationToken ct);
    }
}
