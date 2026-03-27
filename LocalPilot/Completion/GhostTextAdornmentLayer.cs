using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace LocalPilot.Completion
{
    /// <summary>
    /// Defines the MEF-exported adornment layer that GhostTextAdornment uses.
    /// Must be exported so the editor knows about "LocalPilotGhostText" layer.
    /// </summary>
    internal static class GhostTextAdornmentLayer
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("LocalPilotGhostText")]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        [TextViewRole(PredefinedTextViewRoles.Editable)]
        public static AdornmentLayerDefinition EditorAdornmentLayer = null;
    }
}
