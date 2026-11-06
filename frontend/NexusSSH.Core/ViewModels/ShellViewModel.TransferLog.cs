using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

// Das Transfer-Protokoll (TransferJournal): eine Datei im Datenordner, die nur wächst, solange der Benutzer es
// eingeschaltet hat. Von Haus aus aus – die App schreibt nichts mit, wonach niemand gefragt hat.
public sealed partial class ShellViewModel
{
    private TransferJournal? _journal;

    /// <summary>Die Datei des Transfer-Protokolls; <c>null</c>, wenn es hier keines gibt (Tests, Selbsttest).</summary>
    public string? TransferLogFile => _journal?.File;

    /// <summary>Jeder beendete Transfer bekommt eine Zeile im Transfer-Protokoll.</summary>
    public bool KeepTransferLog
    {
        get => Settings.TransferLog;
        set
        {
            if (value != Settings.TransferLog)
            {
                Settings.TransferLog = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Legt fest, wohin das Transfer-Protokoll geschrieben wird. Geschrieben wird erst, wenn es eingeschaltet ist.</summary>
    public void UseTransferLog(string file, TimeProvider? time = null)
    {
        _journal?.Dispose();
        _journal = new TransferJournal(_backend.Transfers, file, () => Settings.TransferLog, time);
        _journal.Failed += detail => _dispatcher.Post(() => Log(L.Get("LogTransferLogFailed", detail), LogSeverity.Warning));
    }

    /// <summary>
    /// Der Ordner, in dem das Protokoll liegt – zum Anzeigen. <c>null</c> mit einem Satz in der Ausgabe, solange
    /// noch nichts protokolliert wurde.
    /// </summary>
    public string? TransferLogFolder()
    {
        if (TransferLogFile is { } file && File.Exists(file))
        {
            return Path.GetDirectoryName(file);
        }

        Log(L.Get("LogTransferLogEmpty"));
        return null;
    }
}
