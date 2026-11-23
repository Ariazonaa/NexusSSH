using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Selbst-Update: Der Hinweis auf eine neue Version bietet an, das Setup zu holen und zu starten. Nur auf Zustimmung,
// nur aus einer Installation, nur eine Datei, die ganz zu dem passt, was GitHub über sie sagt (UpdateSetup,
// UpdateDownloader); starten und prüfen der Signatur tut die App (IUpdateInstaller).
public sealed partial class ShellViewModel
{
    private IUpdateInstaller? _installer;
    private UpdateDownloader? _downloader;
    private string _repository = string.Empty;
    private CancellationTokenSource? _updateCancellation;

    /// <summary>Die App beendet sich, weil das Setup eines Updates läuft – ohne die Frage beim Schließen.</summary>
    public event Action? ExitForUpdate;

    /// <summary>Gerade wird ein Update geladen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    public partial bool IsUpdating { get; private set; }

    /// <summary>Wie weit der Download ist, 0 bis 100.</summary>
    [ObservableProperty]
    public partial double UpdateProgress { get; private set; }

    /// <summary>Warum das letzte Update nicht klappte; leer, wenn nichts schiefging.</summary>
    [ObservableProperty]
    public partial string UpdateProblem { get; private set; } = string.Empty;

    /// <summary>Der Knopf „Jetzt aktualisieren“: ein passendes Setup, eine Installation, und es läuft gerade keins.</summary>
    public bool CanInstallUpdate => !IsUpdating && UpdateSetupFor(AvailableUpdate) is not null;

    partial void OnAvailableUpdateChanged(UpdateInfo? value) => OnPropertyChanged(nameof(CanInstallUpdate));

    /// <summary>Was das Selbst-Update braucht; ohne Aufruf (Tests, Beispieldaten, ein Build ohne Setup) bleibt es beim Link.</summary>
    public void ConfigureUpdates(IUpdateInstaller installer, UpdateDownloader downloader, string repository)
    {
        (_installer, _downloader, _repository) = (installer, downloader, repository);
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    /// <summary>
    /// Fragt, lädt, prüft und startet das Setup; danach beendet sich die App (<see cref="ExitForUpdate"/>). Was
    /// schiefgeht, steht in <see cref="UpdateProblem"/> und im Protokoll – die App läuft dann einfach weiter.
    /// </summary>
    public async Task InstallUpdateAsync()
    {
        if (IsUpdating || AvailableUpdate is not { } update || UpdateSetupFor(update) is not { } asset || _installer is null || _downloader is null)
        {
            return;
        }

        var text = L.Get("UpdateAskText", update.Version, Math.Max(1, (int)Math.Round(asset.Size / 1024.0 / 1024.0)));
        if (CloseQuestion() is { } busy)
        {
            text = $"{text}\n\n{busy}";
        }

        if (!await _prompts.ConfirmAsync(L.Get("UpdateAskTitle", update.Version), text, L.Get("UpdateAskConfirm")))
        {
            return;
        }

        (IsUpdating, UpdateProgress, UpdateProblem) = (true, 0, string.Empty);
        using var cancellation = _updateCancellation = new CancellationTokenSource();
        try
        {
            var path = await _downloader.DownloadAsync(
                asset,
                _installer.DownloadDirectory,
                fraction => _dispatcher.Post(() => UpdateProgress = Math.Round(fraction * 100)),
                cancellation.Token);
            Log(L.Get("UpdateDownloaded", asset.Name, update.Version));
            _installer.Start(path);
            Log(L.Get("UpdateStarted", update.Version));
            ExitForUpdate?.Invoke();
        }
        catch (UpdateException e)
        {
            UpdateProblem = L.Get("UpdateFailed", UpdateFailure(e.Code));
            Log(L.Get("UpdateFailedLog", update.Version, e.Code, e.Message), LogSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            Log(L.Get("UpdateCancelled", update.Version));
        }
        finally
        {
            _updateCancellation = null;
            IsUpdating = false;
        }
    }

    /// <summary>Bricht einen laufenden Download ab; was halb da war, wird gelöscht.</summary>
    public void CancelUpdate() => _updateCancellation?.Cancel();

    private UpdateAsset? UpdateSetupFor(UpdateInfo? update)
        => update is not null && _installer is { CanInstall: true } && _downloader is not null
            ? UpdateSetup.Choose(update, _repository, RuntimeInformation.ProcessArchitecture)
            : null;

    private static string UpdateFailure(string code) => code switch
    {
        "checksum" => L.Get("UpdateFailedChecksum"),
        "size" => L.Get("UpdateFailedSize"),
        "signature" => L.Get("UpdateFailedSignature"),
        "start" => L.Get("UpdateFailedStart"),
        _ => L.Get("UpdateFailedDownload"),
    };
}
