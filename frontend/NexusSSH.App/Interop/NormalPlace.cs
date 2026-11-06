using System.Runtime.InteropServices;
using NexusSSH.Core.Settings;

namespace NexusSSH.App.Interop;

/// <summary>
/// Wo ein maximiertes oder minimiertes Fenster stünde, wenn es normal wäre. <c>AppWindow</c> nennt dann nur den
/// maximierten Platz; ohne das hier ginge ein Fenster, das auf einen anderen Bildschirm gezogen und dort maximiert wurde,
/// auf dem alten wieder auf.
/// </summary>
internal static partial class NormalPlace
{
    public static WindowBounds? Of(nint window)
    {
        var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(window, ref placement))
        {
            return null;
        }

        var normal = placement.Normal;
        return new WindowBounds(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, ref Placement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public Rect Normal;
    }
}
