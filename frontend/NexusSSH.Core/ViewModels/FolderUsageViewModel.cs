using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Ein Eintrag der Belegung: wie viel Platz er braucht und welchen Anteil am Verzeichnis das ist.</summary>
public sealed record UsageRow(string Path, string Name, bool IsDirectory, long Bytes, double Share)
{
    public string SizeText => Format.FileSize(Bytes, isDirectory: false);

    /// <summary>Anteil in Prozent, für den Balken.</summary>
    public double Percent => Math.Round(Share * 100, 1);

    public override string ToString() => $"{Name}, {SizeText}";
}

/// <summary>
/// „Wo ist der Platz hin?“ – was direkt unter einem Verzeichnis des Servers wie viel belegt, das Größte zuerst;
/// in einen Ordner lässt sich hineingehen. Gerechnet wird auf dem Server (<c>du</c>), und nur auf Wunsch: Bei einem
/// großen Baum dauert das.
/// </summary>
public sealed partial class FolderUsageViewModel(IRemoteFileSystem files, string path) : ObservableObject
{
    /// <summary>Mehr Zeilen zeigt die Liste nicht – der Rest steht als Summe darunter.</summary>
    public const int MaxRows = 200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(PathShown))]
    public partial string Path { get; private set; } = RemotePath.Normalize(path);

    public string PathShown => Format.Visible(Path);

    public bool CanGoUp => RemotePath.Parent(Path) is not null;

    [ObservableProperty]
    public partial IReadOnlyList<UsageRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Die Summe des Verzeichnisses, oder warum es keine gibt.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public Task LoadAsync() => ShowAsync(Path);

    public Task OpenAsync(UsageRow row) => row.IsDirectory ? ShowAsync(row.Path) : Task.CompletedTask;

    public Task GoUpAsync() => RemotePath.Parent(Path) is { } parent ? ShowAsync(parent) : Task.CompletedTask;

    private async Task ShowAsync(string target)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Summary = L.Get("UsageBusy");
        try
        {
            var usage = await files.UsageAsync(target);
            var folders = (await files.ListAsync(target)).Where(entry => entry.IsDirectory).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            var own = usage.FirstOrDefault(entry => RemotePath.Normalize(entry.Path) == target);
            var below = usage.Where(entry => RemotePath.Normalize(entry.Path) != target).OrderByDescending(entry => entry.Bytes).ToList();

            // Ohne Zeile für das Verzeichnis selbst (der Server brach ab) gilt die Summe dessen, was er nannte.
            var total = Math.Max(own?.Bytes ?? 0, below.Sum(entry => entry.Bytes));
            Rows =
            [
                .. below.Take(MaxRows).Select(entry =>
                {
                    var name = RemotePath.Name(entry.Path);
                    return new UsageRow(entry.Path, Format.Visible(name), folders.Contains(name), entry.Bytes, total > 0 ? (double)entry.Bytes / total : 0);
                }),
            ];
            Path = target;
            Summary = below.Count == 0 && own is null
                ? L.Get("UsageNone")
                : below.Count > MaxRows
                    ? L.Get("UsageTotalMore", Format.FileSize(total, isDirectory: false), below.Count - MaxRows)
                    : L.Get("UsageTotal", Format.FileSize(total, isDirectory: false));
        }
        catch (RemoteFileException e)
        {
            Summary = FailureText.Describe(e.Code, e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
