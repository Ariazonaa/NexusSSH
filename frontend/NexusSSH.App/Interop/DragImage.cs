using System.Runtime.InteropServices;

namespace NexusSSH.App.Interop;

/// <summary>
/// Das Bild unter dem Mauszeiger beim Ziehen: das Symbol von Windows für eine Datei bzw. einen Ordner. Ein eigenes
/// Bild statt des Standardbilds des Bild-Helfers – das rechnet er aus Formaten aus, die es bei Dateien, die es hier
/// noch nicht gibt, nicht gibt („Shell IDList Array“), und solange er rechnet, nimmt der Explorer nichts an.
/// </summary>
internal static unsafe partial class DragImage
{
    private const int Size = 48;
    private const uint SIID_DOCNOASSOC = 0;
    private const uint SIID_FOLDER = 3;
    private const uint SHGSI_ICON = 0x100;
    private const uint DI_NORMAL = 3;
    private const uint CLR_NONE = 0xFFFFFFFF;

    /// <summary>Gibt dem Bild-Helfer das Bild; <c>false</c>, wenn das nicht ging – dann wird ohne Bild gezogen.</summary>
    public static bool Give(IDragSourceHelper helper, nint dataObject, bool folder)
    {
        var info = new SHSTOCKICONINFO { Size = (uint)sizeof(SHSTOCKICONINFO) };
        if (SHGetStockIconInfo(folder ? SIID_FOLDER : SIID_DOCNOASSOC, SHGSI_ICON, &info) < 0 || info.Icon == 0)
        {
            return false;
        }

        nint bitmap = 0;
        try
        {
            var dc = CreateCompatibleDC(0);
            try
            {
                // 32 Bit von oben nach unten, mit Alpha: so zeichnet DrawIconEx die Ränder des Symbols weich.
                var header = new BITMAPINFOHEADER { HeaderSize = 40, Width = Size, Height = -Size, Planes = 1, BitCount = 32 };
                bitmap = CreateDIBSection(dc, &header, 0, out _, 0, 0);
                if (bitmap == 0)
                {
                    return false;
                }

                var previous = SelectObject(dc, bitmap);
                _ = DrawIconEx(dc, 0, 0, info.Icon, Size, Size, 0, 0, DI_NORMAL);
                _ = SelectObject(dc, previous);
            }
            finally
            {
                _ = DeleteDC(dc);
            }

            // Der Zeiger sitzt unten in der Mitte des Symbols.
            var image = new SHDRAGIMAGE { Width = Size, Height = Size, OffsetX = Size / 2, OffsetY = Size - 6, Bitmap = bitmap, ColorKey = CLR_NONE };
            if (helper.InitializeFromBitmap((nint)(&image), dataObject) < 0)
            {
                return false;
            }

            // Die Bitmap gehört jetzt dem Bild-Helfer.
            bitmap = 0;
            return true;
        }
        finally
        {
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }

            _ = DestroyIcon(info.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHDRAGIMAGE
    {
        public int Width;
        public int Height;
        public int OffsetX;
        public int OffsetY;
        public nint Bitmap;
        public uint ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint HeaderSize;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHSTOCKICONINFO
    {
        public uint Size;
        public nint Icon;
        public int SystemImageIndex;
        public int IconIndex;
        public fixed char Path[260];
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint id, uint flags, SHSTOCKICONINFO* info);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint item);

    [LibraryImport("user32.dll")]
    private static partial int DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint background, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);
}
