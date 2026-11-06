using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

internal static class TerminalLayoutExtensions
{
    /// <summary>Die Richtung der obersten Teilung – wo nichts geteilt ist, <c>true</c>.</summary>
    public static bool RootSideBySide(this TerminalLayout layout) => layout.Tree is not SplitBranch<SessionViewModel> branch || branch.SideBySide;
}
