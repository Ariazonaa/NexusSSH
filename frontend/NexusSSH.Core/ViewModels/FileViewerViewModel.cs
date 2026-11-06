using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was der Betrachter gerade zeigt.</summary>
public enum ViewerMode
{
    Loading,
    Text,
    Hex,
    Image,
    Pdf,

    /// <summary>Die Datei wird im eingebauten Editor bearbeitet.</summary>
    Edit,

    /// <summary>Nichts zu zeigen; warum, steht in <see cref="FileViewerViewModel.Status"/>.</summary>
    Problem,
}

/// <summary>Eine Zeile der Textansicht: der Text ohne Steuerzeichen und seine Einteilung für die Farben.</summary>
public sealed record ViewerLine(string Text, IReadOnlyList<SyntaxSpan> Spans);

/// <summary>
/// Eine Datei des Servers ansehen und – wenn sie klein und Text ist – bearbeiten, ohne ein anderes Programm zu
/// starten. Text und Hex werden in Stücken direkt gelesen (auch aus einer sehr großen Datei), Bilder und PDF wie
/// bisher als Kopie geholt. Der Inhalt ist fremd: Angezeigt wird reiner Text ohne Steuerzeichen; im Editor steht
/// der Inhalt unverändert, damit beim Speichern nichts verloren geht.
/// </summary>
public sealed partial class FileViewerViewModel : ObservableObject
{
    /// <summary>So viel Text zeigt eine Seite.</summary>
    public const int TextPage = 256 * 1024;

    /// <summary>So viele Bytes zeigt eine Seite der Hex-Ansicht (16 je Zeile).</summary>
    public const int HexPage = 8 * 1024;

    /// <summary>Größere Dateien lassen sich hier nicht bearbeiten – dafür gibt es den eigenen Editor.</summary>
    public const long MaxEditSize = 1024 * 1024;

    /// <summary>Größere Bilder und PDF holt der Betrachter nicht.</summary>
    public const long MaxFetchSize = 50L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"];

    private readonly IRemoteFileSystem _files;
    private readonly IUserPrompts _prompts;
    private readonly Func<string, Task<(string? LocalPath, string? Failure)>>? _fetch;
    private readonly string _path;
    private readonly Stack<long> _earlier = new();
    private long _offset;
    private long _next;
    private long _size;
    private (long Size, DateTimeOffset Modified)? _stamp;
    private string _lineEnding = "\n";
    private bool _bom;
    private string _loadedText = string.Empty;

    /// <param name="fetch">Holt die ganze Datei als Kopie auf diesen Rechner (Bild, PDF); ohne das gibt es nur Text und Hex.</param>
    public FileViewerViewModel(IRemoteFileSystem files, IUserPrompts prompts, string server, string path, Func<string, Task<(string? LocalPath, string? Failure)>>? fetch = null)
    {
        (_files, _prompts, _fetch, _path) = (files, prompts, fetch, path);
        Name = Format.Visible(RemotePath.Name(path));
        Title = L.Get("ViewerTitle", Name, server);
        Syntax = SyntaxColors.KindOf(RemotePath.Name(path));
    }

    public string Name { get; }

    public string Title { get; }

    public SyntaxKind Syntax { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsHex), nameof(IsEditing), nameof(ShowsPages), nameof(CanEdit), nameof(CanToggleHex))]
    public partial ViewerMode Mode { get; private set; } = ViewerMode.Loading;

    public bool IsText => Mode == ViewerMode.Text;

    public bool IsHex => Mode == ViewerMode.Hex;

    public bool IsEditing => Mode == ViewerMode.Edit;

    /// <summary>Text und Hex blättern in Seiten.</summary>
    public bool ShowsPages => Mode is ViewerMode.Text or ViewerMode.Hex;

    /// <summary>Die Zeilen der Textansicht.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ViewerLine> Lines { get; private set; } = [];

    /// <summary>Die Seite der Hex-Ansicht als Text: Adresse, 16 Bytes, Zeichen.</summary>
    [ObservableProperty]
    public partial string HexText { get; private set; } = string.Empty;

    /// <summary>Die geholte Kopie für Bild und PDF.</summary>
    [ObservableProperty]
    public partial string? LocalPath { get; private set; }

    /// <summary>Was gerade zu sehen ist („Bytes 0 – 262 144 von 4,2 MB“) oder warum nichts.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    [ObservableProperty]
    public partial bool CanGoOn { get; private set; }

    /// <summary>Der Text im Editor – der Inhalt der Datei, Zeilen mit <c>\r</c> getrennt, wie das Eingabefeld sie führt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string EditText { get; set; } = string.Empty;

    public bool IsDirty => IsEditing && Normalize(EditText) != _loadedText;

    /// <summary>Warum sich die Datei hier nicht bearbeiten lässt; leer, wenn es geht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial string EditObstacle { get; private set; } = string.Empty;

    public bool CanEdit => Mode == ViewerMode.Text && EditObstacle.Length == 0;

    public bool CanToggleHex => Mode is ViewerMode.Text or ViewerMode.Hex;

    /// <summary>Lädt die Datei so, wie sie sich am besten zeigen lässt: Bild, PDF, Text – oder Hex, wenn es kein Text ist.</summary>
    public async Task LoadAsync()
    {
        var extension = Path.GetExtension(RemotePath.Name(_path)).ToLowerInvariant();
        if (_fetch is not null && (extension == ".pdf" || ImageExtensions.Contains(extension)))
        {
            await FetchAsync(extension == ".pdf" ? ViewerMode.Pdf : ViewerMode.Image);
            return;
        }

        await ShowTextAsync(0);
    }

    public Task GoOnAsync()
    {
        if (!CanGoOn || IsBusy)
        {
            return Task.CompletedTask;
        }

        _earlier.Push(_offset);
        return ShowAsync(_next);
    }

    public Task GoBackAsync() => CanGoBack && !IsBusy ? ShowAsync(_earlier.Pop()) : Task.CompletedTask;

    /// <summary>Wechselt zwischen Text und Hex – an derselben Stelle der Datei.</summary>
    public Task ToggleHexAsync()
    {
        if (!CanToggleHex || IsBusy)
        {
            return Task.CompletedTask;
        }

        _earlier.Clear();
        return Mode == ViewerMode.Hex ? ShowTextAsync(0) : ShowHexAsync(_offset - _offset % 16);
    }

    /// <summary>Öffnet die Datei im Editor: den ganzen Inhalt, unverändert.</summary>
    public async Task<bool> EditAsync()
    {
        if (!CanEdit || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var part = await _files.ReadAsync(_path, 0, (int)MaxEditSize + 1);
            if (part.TotalSize > MaxEditSize || Decode(part.Data, strict: true) is not { } text)
            {
                EditObstacle = L.Get("ViewerEditNotHere");
                return false;
            }

            _stamp = await StampAsync();
            _bom = part.Data.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            _lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            _loadedText = Normalize(text);
            EditText = _loadedText;
            Mode = ViewerMode.Edit;
            Status = L.Get("ViewerEditing");
            OnPropertyChanged(nameof(IsDirty));
            return true;
        }
        catch (RemoteFileException e)
        {
            Status = FailureText.Describe(e.Code, e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Speichert den Text des Editors auf dem Server – mit den Zeilenenden und der Kodierung, die die Datei hatte.
    /// Hat sie sich dort inzwischen geändert, entscheidet der Benutzer, ob überschrieben wird.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!IsEditing || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            if (_stamp is { } known && await StampAsync() != known
                && !await _prompts.ConfirmAsync(L.Get("EditConflictTitle", Name), L.Get("ViewerConflictText", Name), L.Get("Overwrite")))
            {
                Status = L.Get("ViewerNotSaved");
                return false;
            }

            var text = Normalize(EditText);
            var bytes = new UTF8Encoding(_bom).GetBytes(text.Replace("\n", _lineEnding, StringComparison.Ordinal));
            await _files.WriteAsync(_path, _bom ? [.. Encoding.UTF8.Preamble, .. bytes] : bytes);
            _loadedText = text;
            _stamp = await StampAsync();
            Status = L.Get("ViewerSaved", DateTime.Now.ToString("T", L.Culture));
            OnPropertyChanged(nameof(IsDirty));
            return true;
        }
        catch (RemoteFileException e)
        {
            Status = FailureText.Describe(e.Code, e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Verlässt den Editor und zeigt wieder die Datei, wie sie auf dem Server liegt.</summary>
    public Task LeaveEditorAsync() => IsEditing ? ShowTextAsync(0) : Task.CompletedTask;

    private Task ShowAsync(long offset) => Mode == ViewerMode.Hex ? ShowHexAsync(offset) : ShowTextAsync(offset);

    private async Task ShowTextAsync(long offset)
    {
        if (offset == 0)
        {
            _earlier.Clear();
        }

        if (await ReadAsync(offset, TextPage) is not { } part)
        {
            return;
        }

        // Kein Text (ein Nullbyte unter den ersten Bytes): Dann sagt die Hex-Ansicht mehr als Zeichensalat.
        if (part.Data.AsSpan(0, Math.Min(part.Data.Length, 8000)).Contains((byte)0))
        {
            EditObstacle = L.Get("ViewerEditNotHere");
            Show(offset, part, ViewerMode.Hex);
            return;
        }

        // Die Seite endet an einem Zeilenende, wenn danach noch etwas kommt – so beginnt die nächste nicht mitten
        // in einer Zeile oder in einem Zeichen.
        var length = part.Data.Length;
        if (offset + length < part.TotalSize && Array.LastIndexOf(part.Data, (byte)'\n') is > 0 and var lineEnd)
        {
            length = lineEnd + 1;
        }

        var text = Decode(part.Data.AsSpan(0, length).ToArray(), strict: false)!;
        Lines =
        [
            .. text.TrimStart('﻿').ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line =>
            {
                var clean = Format.Visible(line.Replace("\t", "    ", StringComparison.Ordinal));
                return new ViewerLine(clean, SyntaxColors.Spans(clean, Syntax));
            }),
        ];
        EditObstacle = part.TotalSize > MaxEditSize ? L.Get("ViewerEditTooLarge", Format.FileSize(MaxEditSize, isDirectory: false))
            : Decode(part.Data, strict: true) is null && offset + part.Data.Length >= part.TotalSize ? L.Get("ViewerEditNotHere")
            : string.Empty;
        Show(offset, part with { Data = part.Data[..length] }, ViewerMode.Text);
    }

    private async Task ShowHexAsync(long offset)
    {
        if (await ReadAsync(offset, HexPage) is { } part)
        {
            Show(offset, part, ViewerMode.Hex);
        }
    }

    private void Show(long offset, FilePart part, ViewerMode mode)
    {
        (_offset, _next, _size) = (offset, offset + part.Data.Length, part.TotalSize);
        if (mode == ViewerMode.Hex)
        {
            HexText = HexDump(part.Data, offset);
        }

        Mode = mode;
        CanGoBack = _earlier.Count > 0;
        CanGoOn = _next < _size && part.Data.Length > 0;
        Status = _size == 0 ? L.Get("ViewerEmpty")
            : CanGoBack || CanGoOn ? L.Get("ViewerRange", offset.ToString("N0", L.Culture), _next.ToString("N0", L.Culture), Format.FileSize(_size, isDirectory: false))
            : Format.FileSize(_size, isDirectory: false);
    }

    private async Task<FilePart?> ReadAsync(long offset, int length)
    {
        IsBusy = true;
        try
        {
            return await _files.ReadAsync(_path, offset, length);
        }
        catch (RemoteFileException e)
        {
            Mode = ViewerMode.Problem;
            Status = FailureText.Describe(e.Code, e.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FetchAsync(ViewerMode mode)
    {
        IsBusy = true;
        try
        {
            var size = (await StampAsync())?.Size ?? 0;
            if (size > MaxFetchSize)
            {
                Mode = ViewerMode.Problem;
                Status = L.Get("PreviewTooLarge", Format.FileSize(size, isDirectory: false));
                return;
            }

            var (local, failure) = await _fetch!(_path);
            if (local is null)
            {
                Mode = ViewerMode.Problem;
                Status = FailureText.OfTransfer(failure);
                return;
            }

            LocalPath = local;
            Mode = mode;
            Status = Format.FileSize(size, isDirectory: false);
        }
        catch (RemoteFileException e)
        {
            Mode = ViewerMode.Problem;
            Status = FailureText.Describe(e.Code, e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Größe und Änderungszeit der Datei, wie die Liste ihres Verzeichnisses sie nennt; `null`, wenn es sie nicht gibt.
    private async Task<(long Size, DateTimeOffset Modified)?> StampAsync()
    {
        var name = RemotePath.Name(_path);
        var entries = await _files.ListAsync(RemotePath.Parent(_path) ?? RemotePath.Root);
        return entries.FirstOrDefault(entry => entry.Name == name) is { } entry ? (entry.Size, entry.Modified) : null;
    }

    // UTF-8 (auch mit BOM); zum Ansehen sonst die westeuropäische Kodierung, zum Bearbeiten (`strict`) nichts –
    // was nicht UTF-8 ist, ließe sich nicht unverändert zurückschreiben.
    private static string? Decode(byte[] bytes, bool strict)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return strict ? null : Encoding.Latin1.GetString(bytes);
        }
    }

    // Zeilenenden einheitlich: Das Eingabefeld führt sie als \r, die Datei als \n oder \r\n.
    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    /// <summary>16 Bytes je Zeile: Adresse, die Bytes hexadezimal, dahinter als Zeichen (was sich nicht drucken lässt, als Punkt).</summary>
    public static string HexDump(ReadOnlySpan<byte> data, long offset)
    {
        var text = new StringBuilder(data.Length * 5);
        for (var line = 0; line < data.Length; line += 16)
        {
            var row = data.Slice(line, Math.Min(16, data.Length - line));
            text.Append((offset + line).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
            for (var index = 0; index < 16; index++)
            {
                text.Append(index < row.Length ? row[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) : "  ").Append(index == 7 ? "  " : " ");
            }

            text.Append(' ');
            foreach (var value in row)
            {
                text.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }
}
