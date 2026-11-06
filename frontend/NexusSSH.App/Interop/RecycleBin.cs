using System.Runtime.InteropServices;

namespace NexusSSH.App.Interop;

/// <summary>
/// Der Papierkorb von Windows – für das Löschen in der lokalen Spalte der Ansicht „Zwei Spalten“. Ohne Rückfrage
/// und ohne Fenster von Windows: Gefragt hat die App schon. Wo ein Laufwerk keinen Papierkorb hat (Netzlaufwerk),
/// löscht Windows endgültig.
/// </summary>
internal static unsafe partial class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>Legt Dateien und Ordner in den Papierkorb; liefert, ob alles ging.</summary>
    public static bool Send(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return true;
        }

        // Die Liste ist ein Text: jeder Pfad mit einer Null beendet, am Ende eine weitere.
        var list = string.Concat(paths.Select(path => Path.GetFullPath(path) + '\0')) + '\0';
        fixed (char* from = list)
        {
            var operation = new SHFILEOPSTRUCTW
            {
                Function = FO_DELETE,
                From = from,
                Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperationW(ref operation) == 0 && operation.AnyOperationsAborted == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCTW
    {
        public nint Window;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref SHFILEOPSTRUCTW operation);
}
