using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NexusSSH.App.Dialogs;
using NexusSSH.Core;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Backend.Native;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App;

/// <summary>Einstieg und Verdrahtung: Einstellungen, Sprache, Backend, Hauptfenster.</summary>
public partial class App : Application
{
    /// <summary>Startet mit Beispieldaten statt mit dem Rust-Kern – zum Vorführen und für die Arbeit an der Oberfläche.</summary>
    private const string DemoArgument = "--demo";

    /// <summary>
    /// Beispieldaten mit allem, was die Prüfläufe brauchen (Tunnel, Links, viele Dateien), statt des Bildes aus dem
    /// Entwurf, das <c>--demo</c> zeigt – für <c>tools/ui-test.ps1</c>. Der Selbsttest nimmt sie von selbst.
    /// </summary>
    private const string DemoFullArgument = "--demo-full";

    /// <summary>
    /// Zeigt das Fenster, ohne ihm den Fokus zu geben – für Prüfungen an der laufenden App, während am Rechner
    /// gearbeitet wird: Das Testfenster drängt sich nicht vor, und fremde Tastendrücke landen nicht darin.
    /// </summary>
    private const string NoActivateArgument = "--no-activate";

    /// <summary>Öffentliches GitHub-Repo, dessen Releases die App auf eine neuere Version prüft. Einzige Stelle zum Ändern.</summary>
    private const string UpdateRepository = "Ariazonaa/NexusSSH";

    /// <summary>Ein wiederverwendeter HttpClient für die Update-Prüfung.</summary>
    private static readonly System.Net.Http.HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Selbsttest für die ausgelieferte Fassung: startet mit Beispieldaten in einem Fenster außerhalb des
    /// Bildschirms, lädt die Ansichten, und beendet sich nach ein paar Sekunden – mit 0, wenn bis dahin keine
    /// Ausnahme aufgetreten ist, sonst mit <see cref="SelfTestFailed"/>. Unter Native AOT zeigen sich manche Fehler
    /// erst zur Laufzeit (eine Liste, die XAML nicht kennt); die CI ruft deshalb die veröffentlichte Exe so auf.
    /// Einstellungen werden dabei nicht gespeichert.
    /// </summary>
    private const string SelfTestArgument = "--self-test";

    /// <summary>
    /// Selbsttest mit echter Verbindung: <c>--self-test-live &lt;port&gt;</c> verbindet mit <c>nexus-testserver</c>
    /// auf 127.0.0.1 und diesem Port, liest die Dateiliste und schickt eine Datei hin und zurück
    /// (<see cref="LiveSelfTest"/>). Nur mit eigenem Datenordner und eigenem <c>~/.ssh</c>
    /// (<c>NEXUSSSH_DATA_DIR</c>, <c>NEXUSSSH_SSH_DIR</c>) – sonst beendet sich die App gleich wieder.
    /// </summary>
    private const string LiveSelfTestArgument = "--self-test-live";

    // Die Zahl der Zeilen hinter --self-test-flood; null, wenn der Schalter fehlt.
    private static int? FloodLines()
    {
        var arguments = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(arguments, argument => argument.Equals("--self-test-flood", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < arguments.Length && int.TryParse(arguments[at + 1], out var lines) && lines is > 0 and <= 10_000_000 ? lines : null;
    }

    // Der Port hinter --self-test-live; null, wenn der Schalter fehlt oder keine Portnummer folgt.
    private static int? LiveSelfTestPort()
    {
        var arguments = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(arguments, argument => argument.Equals(LiveSelfTestArgument, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < arguments.Length && int.TryParse(arguments[at + 1], out var port) && port is > 0 and < 65536 ? port : null;
    }

    private const int SelfTestFailed = 3;

    private static int _loggedExceptions;

    // Die Zeitgeber des Selbsttests müssen jemandem gehören, sonst räumt die Speicherbereinigung sie vor dem Ablauf weg.
    private static readonly List<DispatcherQueueTimer> SelfTestTimers = [];

    private readonly SettingsStore _settingsStore = new(AppDataLocation.SettingsFile);
    private MainWindow? _window;

    public App()
    {
        // Der WebView2 legt seine Benutzerdaten sonst neben die EXE – in einem Installationsordner nicht beschreibbar.
        // Muss gesetzt sein, bevor der erste WebView entsteht.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", AppDataLocation.WebViewData);

        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Zustand des Hauptfensters. Die Ansichten binden direkt hieran.</summary>
    public static ShellViewModel Shell { get; private set; } = null!;

    /// <summary>Alle Dialoge der App (Rückfragen des Backends, Profil-Editor).</summary>
    public static DialogService Dialogs { get; private set; } = null!;

    /// <summary>Läuft die App ohne Benutzer (Selbsttest)? Dann fragt sie nichts.</summary>
    public static bool IsUnattended { get; private set; }

    /// <summary>Gestartet mit <c>--no-activate</c>: ein Prüflauf, der dem Benutzer weder Fokus noch Bildschirm nehmen soll.</summary>
    public static bool IsNoActivate => Environment.GetCommandLineArgs().Contains(NoActivateArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>true</c>, wenn die App mit Beispieldaten läuft (<c>--demo</c>).</summary>
    public static bool IsDemo { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Ein Link kommt als einziges Argument. Steht etwas daneben, hat ein Programm ihn ohne Kodierung
        // weitergereicht, und aus einem `"` darin wurden Schalter der App (`--demo`, `--self-test` …) – dann
        // geschieht nichts, statt einen davon gelten zu lassen.
        var given = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (given.Length > 1 && given.Any(LaunchRequest.IsLink))
        {
            Exit();
            return;
        }

        var settings = _settingsStore.Load();

        // Sprache festlegen, bevor irgendein XAML mit {loc:Loc …} geladen wird.
        L.Init(settings.Language);

        // Ebenso die Akzentfarbe: Die Pinsel von WinUI lesen sie beim ersten Gebrauch.
        Ui.ApplyAccent(AccentColors.Find(settings.Accent).Hex);
        Ui.ApplyDensity(settings.Compact);

        var selfTest = Environment.GetCommandLineArgs().Contains(SelfTestArgument, StringComparer.OrdinalIgnoreCase);

        // Der Selbsttest mit echter Verbindung legt ein Profil an und merkt sich einen Host-Schlüssel: nie in den
        // Daten des Benutzers.
        var live = LiveSelfTestPort();
        IsUnattended = selfTest || live is not null;
        if (live is not null && (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUSSSH_DATA_DIR")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUSSSH_SSH_DIR"))))
        {
            Environment.Exit(SelfTestFailed);
        }

        var full = selfTest || Environment.GetCommandLineArgs().Contains(DemoFullArgument, StringComparer.OrdinalIgnoreCase);
        IsDemo = full || Environment.GetCommandLineArgs().Contains(DemoArgument, StringComparer.OrdinalIgnoreCase);
        var showcase = IsDemo && !full;

        // Ein Start mit Ziel (Link, Kommandozeile, Sprungliste): Läuft die App schon, bekommt sie das Ziel, und
        // dieser Start endet hier – vor dem Kern, der sonst ein zweites Mal liefe.
        var launch = IsUnattended || IsDemo ? null : LaunchRequest.FromArguments(given);
        if (launch is not null && Interop.LaunchPipe.TryHandOver(given))
        {
            Exit();
            return;
        }

        // Ab hier nimmt diese App weitere Starts mit Ziel an – so früh wie möglich, damit ein zweiter Klick
        // während des Starts nicht eine zweite App öffnet. Behandelt wird erst, wenn das Fenster steht (die
        // Aufträge laufen über den Thread der Oberfläche, also nach OnLaunched).
        if (!IsUnattended && !IsDemo)
        {
            Interop.LaunchPipe.Listen(DispatcherQueue.GetForCurrentThread(), request => _window?.WhenLoaded(() =>
            {
                _window.BringToFront();
                OpenLaunch(request);
            }));
        }

        INexusBackend backend;
        try
        {
            backend = IsDemo
                ? new MockBackend(TimeProvider.System, showcase)
                : new CoreBackend(new NativeCoreTransport(AppDataLocation.Root, AppDataLocation.SshDirectory, WriteCrashLog), AppDataLocation.TransfersFile);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            // Ohne Kern gibt es nichts zu tun; Beispieldaten unterzuschieben wäre irreführend.
            WriteCrashLog(exception);
            MessageBoxW(0, L.Get("CoreStartFailed", exception.Message), "NexusSSH", 0x10 /* MB_ICONERROR */);
            Exit();
            return;
        }

        // Rückfragen erscheinen in dem Fenster, das zuletzt vorn war – auch in dem eines abgelösten Terminals.
        Dialogs = new DialogService(() => Interop.TerminalWindows.ActiveRoot ?? _window?.ContentRoot);
        // Dateien, die zum Bearbeiten vom Server geholt werden, liegen in einem Ordner je Prozess. Was ein
        // abgestürzter Prozess dort hinterlassen hat, räumt der nächste Start weg.
        RemoteEditor.RemoveStale(AppDataLocation.EditRoot, IsRunning);
        DropFetcher.RemoveStale(AppDataLocation.EditRoot, IsRunning);
        // Update-Prüfung nur im echten Betrieb: nicht mit Beispieldaten und nicht im Selbsttest (kein Netz).
        var updates = IsDemo || selfTest || live is not null ? null : new GitHubUpdateService(UpdateHttp, UpdateRepository);
        Shell = new ShellViewModel(
            backend,
            new UiDispatcher(DispatcherQueue.GetForCurrentThread()),
            settings,
            // Im Selbsttest mit echter Verbindung antwortet niemand auf Dialoge: Vertraut wird allein dem Testserver.
            live is { } testPort ? new SelfTestPrompts(testPort) : Dialogs,
            new ProcessEditorHost(() => settings.EditorPath, () => settings.ComparePath),
            RemoteEditor.WorkDirectory(AppDataLocation.EditRoot),
            DropFetcher.WorkDirectory(AppDataLocation.EditRoot),
            updates,
            // „Später“ für das selbsttätige Neuverbinden: ein Timer, der einmal feuert.
            (delay, action) => TimeProvider.System.CreateTimer(_ => action(), null, delay, Timeout.InfiniteTimeSpan));

        // Das Transfer-Protokoll gibt es nicht im Selbsttest; geschrieben wird es nur, wenn es eingeschaltet ist.
        if (!IsUnattended)
        {
            Shell.UseTransferLog(AppDataLocation.TransferLogFile);
        }

        // Erreichbarkeit auf der Startseite: nur im echten Betrieb, und nur wenn sie eingeschaltet ist.
        if (!IsDemo && live is null)
        {
            Shell.UseReachability(new TcpReachability());
        }

        Shell.Log(L.Get(IsDemo ? "LogStartedDemo" : "LogStarted", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?"));

        // Der Datenordner bestimmt, was die App startet und wem sie traut – dürfen dort andere schreiben, soll der
        // Benutzer das wissen. Nachgesehen wird nebenher: Die Namen der Konten zu holen kann in einer Domäne dauern.
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            if (Interop.DataFolderAcl.OthersWhoCanWrite(AppDataLocation.Root) is { Count: > 0 } others)
            {
                queue.TryEnqueue(() => Shell.Log(L.Get("LogDataFolderShared", AppDataLocation.Root, string.Join(", ", others)), LogSeverity.Warning));
            }
        });

        // Vor dem Fenster: Scheitert schon dessen Aufbau, soll der Selbsttest das melden statt ewig zu warten.
        if (selfTest)
        {
            RunSelfTest();
        }
        else if (live is { } port)
        {
            RunLiveSelfTest(port, backend.Transfers);
        }

        // Ohne Aktivierung gestartet heißt: Das Fenster nimmt auch später niemandem die Tastatur.
        if (Environment.GetCommandLineArgs().Contains(NoActivateArgument, StringComparer.OrdinalIgnoreCase))
        {
            Interop.ForegroundGuard.Keep();
        }

        _window = new MainWindow();

        // Terminals in eigenen Fenstern; vor dem Wiederherstellen der Tabs, das solche Fenster wieder aufmacht.
        Interop.TerminalWindows.Watch(Shell, quiet: IsUnattended || Environment.GetCommandLineArgs().Contains(NoActivateArgument, StringComparer.OrdinalIgnoreCase));
        _window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                Interop.TerminalWindows.NoteActive(null);
            }
        };

        // Nach einem Update einmal zeigen, was neu ist – nicht beim ersten Start, nicht im Selbsttest, nicht mit
        // Beispieldaten (dort bliebe die gemerkte Version ohnehin nicht).
        if (!IsUnattended && !IsDemo)
        {
            Shell.NoteVersion(typeof(App).Assembly.GetName().Version?.ToString(3) ?? string.Empty);

            // Lauscht nur, solange die Einstellung an ist.
            Shell.WatchMcp(NexusSSH.Core.Mcp.McpPipe.Name(AppDataLocation.Root));
        }
        _window.Closed += (_, _) =>
        {
            Shell.StopMcp();

            // Mit Beispieldaten bleibt in den Einstellungen nichts zurück, was auf Profile verweist.
            if (!IsDemo)
            {
                Shell.RememberOpenSessions();
            }

            // Ebenso nicht die Befehle aus dem Bild der Beispieldaten.
            if (showcase && MockBackend.IsShowcase(settings.Snippets))
            {
                settings.Snippets = [];
            }

            if (!selfTest)
            {
                _settingsStore.Save(settings);
            }

            // Die Fenster abgelöster Terminals gehen mit – erst jetzt, damit oben noch steht, welche es gab.
            Interop.TerminalWindows.CloseAll();

            // Was aus dem Dateibrowser kopiert wurde, ließe sich ohne die App nicht mehr einfügen.
            Interop.OleClipboard.Clear();

            // Laufende Transfers anhalten, sodass sie sich nach dem nächsten Start fortsetzen lassen – schlösse
            // zuerst ihre Sitzung, wären sie abgebrochen. Dann die Sitzungen ordentlich beenden, dann den Kern:
            // danach ruft er nicht mehr zurück.
            backend.Transfers.Suspend();
            foreach (var session in Shell.Sessions.ToList())
            {
                session.Dispose();
            }

            (backend as IDisposable)?.Dispose();
            Shell.Dispose();
        };
        // Mit --self-test-visible bleibt das Fenster des Selbsttests auf dem Bildschirm (ohne den Fokus zu nehmen):
        // Nur dann zeichnet das Terminal wirklich – für Messungen, die das Zeichnen einschließen sollen.
        var visible = Environment.GetCommandLineArgs().Contains("--self-test-visible", StringComparer.OrdinalIgnoreCase);
        if ((selfTest || live is not null) && !visible)
        {
            // Weit außerhalb jedes Bildschirms und nicht in der Taskleiste: Der Test stört niemanden.
            _window.AppWindow.IsShownInSwitchers = false;
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            _window.AppWindow.Show(activateWindow: false);
        }
        else if (visible || Environment.GetCommandLineArgs().Contains(NoActivateArgument, StringComparer.OrdinalIgnoreCase))
        {
            _window.AppWindow.Show(activateWindow: false);
        }
        else
        {
            _window.Activate();
            _window.RestoreMaximized();
        }

        if (showcase)
        {
            // Das Bild aus dem Entwurf: drei Tabs, der erste vorn, und die sechs Befehle rechts – die nur, wenn es
            // noch keine gibt, und sie bleiben nicht in den Einstellungen (siehe unten beim Schließen).
            var tabs = MockBackend.ShowcaseTabs.Select(id => Shell.Profiles.FirstOrDefault(profile => profile.Id == id)).OfType<NexusSSH.Core.Models.ServerProfile>().Select(Shell.OpenSession).ToList();
            Shell.ActiveSession = tabs.FirstOrDefault();

            // Ein Tab verbindet erst, wenn sein Terminal die Größe meldet – das tut nur das sichtbare. Im Bild
            // stehen aber auch die übrigen da (db02 verbunden, nas im Aufbau): Die Beispieldaten starten sie, sobald
            // das Fenster steht und ihre Ausgabe einen Hörer hat (TerminalHost hebt sie auf, bis die Seite sie
            // zeigt). Echte Sitzungen bleiben bei der Regel.
            _window.WhenLoaded(() =>
            {
                foreach (var tab in tabs.Skip(1))
                {
                    tab.Start();
                }
            });
            if (Shell.Snippets.Count == 0)
            {
                Shell.SetSnippets(MockBackend.ShowcaseSnippets);
            }
        }
        else if (IsDemo && Shell.SearchProfiles(null) is [var first, ..])
        {
            Shell.OpenSession(first);
        }
        else if (!IsDemo)
        {
            Shell.RestoreOpenSessions();

            // Leise im Hintergrund nachsehen, ob es eine neuere Version gibt (höchstens 1×/Tag, abschaltbar).
            _ = Shell.CheckForUpdatesAsync(manual: false);
        }

        if (!IsUnattended && !IsDemo)
        {
            // Das Ziel dieses Starts, sobald Dialoge gehen.
            if (launch is not null)
            {
                _window.WhenLoaded(() => OpenLaunch(launch));
            }

            // Die zuletzt benutzten Server in der Sprungliste.
            void ShowJumpList() => Interop.JumpList.Show(Shell.RecentServers(), L.Get("JumpListRecent"));
            Shell.RecentServersChanged += () => queue.TryEnqueue(ShowJumpList);
            ShowJumpList();
        }
    }

    private static async void OpenLaunch(LaunchRequest request)
    {
        try
        {
            await Shell.OpenLaunchAsync(request);
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    // Erst die Ansichten laden lassen, dann benutzen, was Listen an XAML gibt, dann mit dem Ergebnis beenden. Die
    // Teilungen prüft die Terminal-Seite nebenher (MainWindow.ExerciseSplitsAsync); auf sie wird gewartet, aber nicht
    // länger, als das Skript dem Selbsttest gibt.
    private void RunSelfTest()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        Task<string?>? splits = null;
        var exercise = queue.CreateTimer();
        exercise.Interval = TimeSpan.FromSeconds(3);
        exercise.IsRepeating = false;
        exercise.Tick += (_, _) =>
        {
            try
            {
                var window = _window ?? throw new InvalidOperationException("The main window was not created.");
                window.ExerciseForSelfTest();
                splits = window.ExerciseSplitsAsync();
            }
            catch (Exception exception)
            {
                WriteCrashLog(exception);
            }
        };
        exercise.Start();

        var finish = queue.CreateTimer();
        finish.Interval = TimeSpan.FromSeconds(7);
        finish.IsRepeating = false;
        finish.Tick += async (_, _) =>
        {
            if (splits is { } check)
            {
                try
                {
                    var problem = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(40))) == check
                        ? await check
                        : "the split check did not finish in time";
                    if (problem is not null)
                    {
                        WriteCrashLog(new InvalidOperationException($"self-test: {problem}"));
                    }
                }
                catch (Exception exception)
                {
                    WriteCrashLog(exception);
                }
            }

            Environment.Exit(_loggedExceptions == 0 ? 0 : SelfTestFailed);
        };
        finish.Start();
        SelfTestTimers.AddRange([exercise, finish]);
    }

    // Erst das Fenster entstehen lassen, dann verbinden, die Dateiliste lesen und eine Datei hin- und zurückschicken.
    // Das Ergebnis steht in app.log; die Zahl am Ende sagt dem Skript, ob es ging.
    private void RunLiveSelfTest(int port, ITransferQueue transfers)
    {
        var start = DispatcherQueue.GetForCurrentThread().CreateTimer();
        start.Interval = TimeSpan.FromSeconds(3);
        start.IsRepeating = false;
        start.Tick += async (_, _) =>
        {
            string? problem;
            try
            {
                var scratch = Path.Combine(AppDataLocation.Root, "self-test");
                problem = await LiveSelfTest.RunAsync(Shell, transfers, port, scratch, TimeSpan.FromSeconds(20));
            }
            catch (Exception exception)
            {
                WriteCrashLog(exception);
                problem = exception.Message;
            }

            // Das Terminal einmal in ein eigenes Fenster und zurück: ein zweites Fenster mit einer zweiten
            // Terminal-Seite, und die Ausgabe muss danach wieder im Hauptfenster ankommen (die Flut unten prüft es).
            if (problem is null && Shell.ActiveSession is { } moved)
            {
                try
                {
                    problem = await MoveTerminalForSelfTestAsync(moved);
                }
                catch (Exception exception)
                {
                    WriteCrashLog(exception);
                    problem = exception.Message;
                }
            }

            // Auf Wunsch (--self-test-flood <zeilen>) auch das Tempo des Terminals: als Zahl in eine Datei, die
            // das Skript mit seiner Marke vergleicht.
            if (problem is null && FloodLines() is { } lines && Shell.ActiveSession is { } session && Controls.TerminalHost.Current is { } terminal)
            {
                try
                {
                    var measured = await LiveSelfTest.FloodAsync(Shell, session, lines, () => terminal.Backlog(session.Id), TimeSpan.FromSeconds(60));
                    if (measured is var (milliseconds, bytes))
                    {
                        var seconds = Math.Max(milliseconds, 1) / 1000.0;
                        var json = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{ \"lines\": {lines}, \"bytes\": {bytes}, \"ms\": {milliseconds}, \"linesPerSecond\": {lines / seconds:0}, \"mbPerSecond\": {bytes / 1048576.0 / seconds:0.0} }}");
                        await File.WriteAllTextAsync(Path.Combine(AppDataLocation.Root, "self-test", "flood.json"), json);
                    }
                    else
                    {
                        problem = "the terminal did not finish the flood in time";
                    }
                }
                catch (Exception exception)
                {
                    WriteCrashLog(exception);
                    problem = exception.Message;
                }
            }

            // Ein lokales Terminal: eine Shell dieses Rechners über den Kern, mit einer Antwort, die sich nicht aus
            // den Tasten allein ergibt – und der Tab geht wieder zu.
            if (problem is null)
            {
                try
                {
                    problem = await LiveSelfTest.LocalTerminalAsync(Shell, TimeSpan.FromSeconds(30));
                }
                catch (Exception exception)
                {
                    WriteCrashLog(exception);
                    problem = exception.Message;
                }
            }

            if (problem is not null)
            {
                WriteCrashLog(new InvalidOperationException($"self-test-live: {problem}"));
            }

            Environment.Exit(problem is null && _loggedExceptions == 0 ? 0 : SelfTestFailed);
        };
        start.Start();
        SelfTestTimers.Add(start);
    }

    // Löst das Terminal der Sitzung in ein eigenes Fenster und holt es zurück; nennt, woran es scheiterte.
    private static async Task<string?> MoveTerminalForSelfTestAsync(SessionViewModel session)
    {
        const string CountTerminals = "document.querySelectorAll('.terminal-frame').length";
        static async Task<bool> UntilAsync(Func<Task<bool>> reached)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (await reached())
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        if (!Shell.Detach(session))
        {
            return "the terminal could not be detached";
        }

        if (!await UntilAsync(async () => Interop.TerminalWindows.All.FirstOrDefault(window => ReferenceEquals(window.Session, session)) is { } window
            && await window.Host.EvaluateAsync(CountTerminals) == "1"))
        {
            return "the detached window did not show the terminal";
        }

        Shell.Reattach(session);
        return await UntilAsync(async () => Interop.TerminalWindows.All.Count == 0
            && Controls.TerminalHost.Current is { } main
            && await main.EvaluateAsync(CountTerminals) is not ("" or "0"))
            ? null
            : "the terminal did not come back to the main window";
    }

    /// <summary>Hält eine unerwartete Ausnahme in <c>logs\app.log</c> fest. Von jedem Thread aus aufrufbar.</summary>
    internal static void WriteCrashLog(Exception exception)
    {
        Interlocked.Increment(ref _loggedExceptions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppDataLocation.LogFile)!);
            File.AppendAllText(AppDataLocation.LogFile, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Wenn nicht einmal das Protokoll schreibbar ist, bleibt nichts zu tun.
        }
    }

    private static bool IsRunning(int process)
    {
        try
        {
            using var found = System.Diagnostics.Process.GetProcessById(process);
            return !found.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);

    private sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
    {
        public void Post(Action action)
        {
            if (queue.HasThreadAccess)
            {
                action();
            }
            else
            {
                queue.TryEnqueue(() => action());
            }
        }
    }
}


