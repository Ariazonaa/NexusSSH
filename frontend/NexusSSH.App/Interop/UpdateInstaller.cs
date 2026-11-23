using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Interop;

/// <summary>
/// Das Setup eines Updates starten – nur, wenn die App aus der Installation läuft, die das Setup eingetragen hat.
/// Vorher die Signatur: Ist das Setup signiert, muss sie gültig sein; ist die laufende App signiert, muss es das Setup
/// auch sein. Solange die Releases unsigniert sind, bleibt allein der SHA-256, den GitHub nennt (UpdateDownloader).
/// </summary>
internal sealed partial class UpdateInstaller : IUpdateInstaller
{
    // Der Eintrag zur Deinstallation, den Inno Setup anlegt (AppId in installer/NexusSSH.iss, nie ändern).
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E2B1A-93D5-4F60-A8E7-2B6D0C9F1E34}_is1";

    private readonly bool? _allUsers;

    public UpdateInstaller(string downloadDirectory)
    {
        DownloadDirectory = downloadDirectory;
        _allUsers = InstalledHere(Registry.CurrentUser) ? false : InstalledHere(Registry.LocalMachine) ? true : null;
    }

    public bool CanInstall => _allUsers is not null;

    public string DownloadDirectory { get; }

    public void Start(string setupPath)
    {
        CheckSignature(setupPath);
        try
        {
            // Still, aber mit Fortschrittsfenster; im Modus der bisherigen Installation. /RELAUNCH startet die App danach.
            var mode = _allUsers == true ? "/ALLUSERS" : "/CURRENTUSER";
            using var process = Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH {mode}",
            });
            if (process is null)
            {
                throw new UpdateException("start", "the setup did not start");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException("start", e.Message);
        }
    }

    // Läuft die App aus dem Ordner, den dieser Eintrag nennt?
    private static bool InstalledHere(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location && location.Length > 0
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Authenticode über WinVerifyTrust, ohne Oberfläche und ohne Abruf der Sperrlisten (das Setup soll auch ohne Netz
    // nach dem Download starten). Keine Signatur ist heute erlaubt – außer die laufende App ist selbst signiert.
    private static void CheckSignature(string path)
    {
        var setup = Verify(path);
        if (setup == Trusted)
        {
            return;
        }

        if (setup != NoSignature)
        {
            throw new UpdateException("signature", $"WinVerifyTrust 0x{setup:X8}");
        }

        if (Verify(Environment.ProcessPath ?? string.Empty) == Trusted)
        {
            throw new UpdateException("signature", "the running app is signed, the setup is not");
        }
    }

    private const uint Trusted = 0;
    private const uint NoSignature = 0x800B0100;

    private static unsafe uint Verify(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            return NoSignature;
        }

        fixed (char* name = path)
        {
            var file = new FileInfo { Size = (uint)sizeof(FileInfo), FilePath = (nint)name };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = (nint)(&file),
                StateAction = 1, // WTD_STATEACTION_VERIFY
            };
            var action = GenericVerifyV2;
            var result = (uint)WinVerifyTrust(-1, ref action, ref data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(-1, ref action, ref data);
            return result;
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
}
