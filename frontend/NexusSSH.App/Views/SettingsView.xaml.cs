using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.Core.Backend.Native;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;
using Windows.System;

namespace NexusSSH.App.Views;

public sealed partial class SettingsView : UserControl
{
    private bool _fillingMcp;

    public SettingsView()
    {
        InitializeComponent();
        Shell.PropertyChanged += OnShellPropertyChanged;
        NavList.SelectedIndex = 0;
        NameForScreenReaders();

        AccentBox.ItemsSource = AccentColors.All;
        ThemeBox.ItemsSource = Shell.Themes;
        ThemeBox.SelectedItem = Shell.TerminalTheme;
        FontBox.ItemsSource = TerminalThemes.Fonts;
        FontBox.Loaded += (_, _) => ShowFont();
        FontSizeBox.Minimum = TerminalThemes.MinFontSize;
        FontSizeBox.Maximum = TerminalThemes.MaxFontSize;
        FontSizeBox.Value = Shell.TerminalFontSize;
        UiZoomBox.Minimum = AppSettings.MinUiZoom;
        UiZoomBox.Maximum = AppSettings.MaxUiZoom;
        UiZoomBox.Value = Shell.UiZoom;
        ShowChosen();
        AddResetButtons();
        ShowMarks();
        ShowExcludePatterns();
        McpSetupText.Text = $"claude mcp add --scope user nexusssh -- \"{Path.Combine(AppContext.BaseDirectory, "nexusssh-mcp.exe")}\"";

        AppVersionText.Text = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        CoreVersionText.Text = NativeCore.Probe() switch
        {
            null => L.Get("CoreMissing"),
            (var version, NativeCore.ExpectedAbi) => version,
            var (version, abi) => L.Get("CoreAbiMismatch", version, abi, NativeCore.ExpectedAbi),
        };
    }

    public ShellViewModel Shell => App.Shell;

    private AppSettings Settings => Shell.Settings;

    /// <summary>Zeigt die eingestellte Sprache – nach einem Import, der sie geändert haben kann.</summary>
    public void ShowLanguage() => LanguageBox.SelectedIndex = (int)Settings.Language;

    /// <summary>Gibt dem ersten Feld den Fokus – nach dem Öffnen, damit Esc und Tab gleich hier ankommen.</summary>
    public void FocusFirst() => CloseButton.Focus(FocusState.Programmatic);

    // Links gewählt, rechts gezeigt: Der Tag des Eintrags nennt die Seite. Ein neuer Bereich braucht einen Eintrag
    // in NavList, sein StackPanel „Page…“ und eine Zeile hier.
    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListViewItem { Tag: string chosen })
        {
            return;
        }

        // Ein Bereich, links angeklickt, beendet die Suche: Sie zeigt alle Bereiche zugleich.
        if (IsSearching)
        {
            SearchBox.Text = string.Empty;
        }

        (string Name, StackPanel Page)[] pages =
        [
            ("General", PageGeneral), ("Look", PageLook), ("Connections", PageConnections), ("Alerts", PageAlerts),
            ("Terminal", PageTerminal), ("Files", PageFiles), ("Shortcuts", PageShortcuts), ("Data", PageData),
            ("Mcp", PageMcp), ("About", PageAbout),
        ];
        foreach (var (name, page) in pages)
        {
            page.Visibility = Ui.VisibleIf(name == chosen);
        }

        if (chosen == "Mcp")
        {
            ShowMcpServers();
        }

        PageScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    // KI-Zugriff: je Server ein Kästchen. Gefüllt, wenn die Seite aufgeht – die Liste der Server ändert sich anderswo.
    private void ShowMcpServers()
    {
        _fillingMcp = true;
        McpServerList.Children.Clear();
        foreach (var profile in Shell.McpCandidates)
        {
            var box = new CheckBox { Content = Core.Format.Visible(profile.Name), IsChecked = Shell.IsMcpServer(profile), Tag = profile.Id };
            box.Checked += OnMcpServerToggled;
            box.Unchecked += OnMcpServerToggled;
            McpServerList.Children.Add(box);
        }

        McpServersNone.Visibility = Ui.VisibleIf(McpServerList.Children.Count == 0);
        _fillingMcp = false;
        ShowMcpAllowed();
    }

    // Je erlaubtem Befehl eine Zeile: Server, der Text genau so, wie er läuft, und ein Knopf zum Entfernen.
    private void ShowMcpAllowed()
    {
        McpAllowedList.Children.Clear();
        foreach (var (server, entry) in Shell.McpAllowedCommandList)
        {
            var command = string.Join(" ⏎ ", entry.Command.Split('\n').Select(Core.Format.Visible));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{Core.Format.Visible(server.Name)}: {command}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Tag = entry };
            var name = L.Get("McpAllowedRemove", command);
            AutomationProperties.SetName(remove, name);
            ToolTipService.SetToolTip(remove, name);
            remove.Click += OnMcpAllowedRemoveClicked;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            McpAllowedList.Children.Add(row);
        }

        McpAllowedNone.Visibility = Ui.VisibleIf(McpAllowedList.Children.Count == 0);
        McpAllowedClear.Visibility = Ui.VisibleIf(McpAllowedList.Children.Count > 0);
    }

    private void OnMcpAllowedRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: McpAllowedCommand entry })
        {
            // Der Knopf verschwindet mit seiner Zeile; der Fokus geht zur nächsten, sonst zur Frist darüber.
            Shell.ForgetMcpCommand(entry);
            ShowMcpAllowed();
            Control next = McpAllowedList.Children is [Grid { Children: [_, Button first] }, ..] ? first : McpPromptBox;
            next.Focus(FocusState.Programmatic);
        }
    }

    private void OnMcpAllowedClearClicked(object sender, RoutedEventArgs e)
    {
        Shell.ForgetMcpCommands();
        ShowMcpAllowed();
    }

    private void OnMcpPromptChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpPromptBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            Shell.McpPromptSeconds = seconds;
        }
    }

    private void OnMcpServerToggled(object sender, RoutedEventArgs e)
    {
        if (!_fillingMcp && sender is CheckBox { Tag: string id } box)
        {
            Shell.SetMcpServer(id, box.IsChecked == true);
            ShowMcpAllowed();
        }
    }

    private void OnCopyMcpSetupClicked(object sender, RoutedEventArgs e)
    {
        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        Interop.TextClipboard.Set(window, McpSetupText.Text, html: null, rtf: null);
    }

    // Ausschlüsse: ein Muster je Zeile. Das Textfeld von WinUI trennt Zeilen mit einem Wagenrücklauf; übernommen
    // wird beim Verlassen des Felds – was kein Muster sein kann, fällt dabei weg und stand vorher unter dem Feld.
    private void ShowExcludePatterns() => ExcludePatternsBox.Text = Shell.ExcludePatternsText.Replace('\n', '\r');

    private void OnExcludePatternsChanged(object sender, TextChangedEventArgs e)
    {
        ExcludePatternsError.Text = ShellViewModel.ExcludePatternsRejected(ExcludePatternsBox.Text.Replace('\r', '\n'));
        ExcludePatternsError.Visibility = Ui.VisibleIfText(ExcludePatternsError.Text);
    }

    private void OnExcludePatternsLostFocus(object sender, RoutedEventArgs e)
    {
        Shell.ExcludePatternsText = ExcludePatternsBox.Text.Replace('\r', '\n');
        ShowExcludePatterns();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Farbschema, Schrift und Größe lassen sich auch über die Palette, per Tastatur und Mausrad ändern.
            // Ein Schema kam dazu oder fiel weg: Die Liste entsteht neu, die Auswahl bleibt, was gilt.
            // Zurück zur Vorgabe oder ein Import: Das Feld zeigt, was jetzt gilt.
            case nameof(ShellViewModel.ExcludePatternsText):
                ShowExcludePatterns();
                break;

            // „Immer erlauben“ kommt aus einem Dialog, während die Seite steht; und wer sie wieder öffnet, landet auf
            // dem Bereich von vorhin, ohne dass die Auswahl wechselt.
            case nameof(ShellViewModel.McpAllowedCommandList):
                ShowMcpAllowed();
                break;

            case nameof(ShellViewModel.IsSettingsOpen) when Shell.IsSettingsOpen && ReferenceEquals(NavList.SelectedItem, NavMcp):
                ShowMcpServers();
                break;

            case nameof(ShellViewModel.Themes):
                ThemeBox.ItemsSource = Shell.Themes;
                ThemeBox.SelectedItem = Shell.TerminalTheme;
                break;

            case nameof(ShellViewModel.TerminalTheme):
                ThemeBox.SelectedItem = Shell.TerminalTheme;
                break;

            case nameof(ShellViewModel.TerminalFont):
                ShowFont();
                break;

            case nameof(ShellViewModel.TerminalFontSize):
                FontSizeBox.Value = Shell.TerminalFontSize;
                break;

            case nameof(ShellViewModel.UiZoom):
                UiZoomBox.Value = Shell.UiZoom;
                break;

            // „Zurück zur Vorgabe“: Was diese Seite selbst in ihre Felder schreibt, steht dort noch wie vorher.
            case nameof(ShellViewModel.ResettableSettings):
                ShowChosen();
                break;
        }

        ShowMarks();
    }

    // Eine der vorgeschlagenen Schriften ist ein Eintrag der Liste, jede andere steht als Text im Feld.
    private void ShowFont()
    {
        var font = Shell.TerminalFont;
        if (TerminalThemes.Fonts.Contains(font))
        {
            FontBox.SelectedItem = font;
        }
        else
        {
            FontBox.SelectedItem = null;
            FontBox.Text = font;
        }
    }

    private void OnChangeShortcutClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutRow row)
        {
            _ = ChangeShortcutAsync(row);
        }
    }

    private async Task ChangeShortcutAsync(ShortcutRow row)
    {
        var (chosen, chord) = await App.Dialogs.AskShortcutAsync(row, candidate => Shell.ShortcutProblem(row.Id, candidate));
        if (chosen)
        {
            Shell.SetShortcut(row.Id, chord);
        }
    }

    // Die Vorgabe kann inzwischen ein anderer Befehl haben – dann sagt eine Meldung, welcher.
    private void OnResetShortcutClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutRow row && Shell.ResetShortcut(row.Id) is { } problem)
        {
            _ = App.Dialogs.ShowMessageAsync(row.Title, problem);
        }
    }

    private void OnResetShortcutsClicked(object sender, RoutedEventArgs e) => Shell.ResetShortcuts();

    // Ein NumberBox rechnet mit double (und NaN für „leer“); die Einstellung ist eine ganze Zahl KiB/s.
    private void SetTransferLimit(double value) => Shell.TransferLimitKib = double.IsFinite(value) ? (int)Math.Clamp(value, 0, AppSettings.MaxTransferLimitKib) : 0;

    private void SetLetterSpacing(double value) => Shell.TerminalLetterSpacing = double.IsFinite(value) ? (int)Math.Round(value) : 0;

    // Die Einträge stehen im XAML; ihr Tag ist die Form, wie die Terminal-Seite sie kennt.
    private void OnCursorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CursorBox.SelectedItem is ComboBoxItem { Tag: string style })
        {
            Shell.TerminalCursor = style;
        }
    }

    // Die Einträge stehen im XAML; ihr Tag ist der Wert, den der Kern kennt.
    private void OnMoshPredictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MoshPredictBox.SelectedItem is ComboBoxItem { Tag: string prediction })
        {
            Shell.MoshPredict = prediction;
        }
    }

    private void SetX11Display(double value) => Shell.X11Display = double.IsFinite(value) ? (int)Math.Clamp(value, 0, AppSettings.MaxX11Display) : 0;

    private void SetKeepAliveSeconds(double value) => Shell.KeepAliveSeconds = double.IsFinite(value) ? (int)Math.Clamp(value, 0, 3600) : AppSettings.DefaultKeepAliveSeconds;

    private void SetKeepAliveMissed(double value) => Shell.KeepAliveMissed = double.IsFinite(value) ? (int)Math.Clamp(value, 1, AppSettings.MaxKeepAliveMissed) : AppSettings.DefaultKeepAliveMissed;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Shell.IsSettingsOpen = false;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            // Esc leert zuerst die Suche; erst das zweite schließt die Seite.
            if (IsSearching)
            {
                SearchBox.Text = string.Empty;
            }
            else
            {
                Shell.IsSettingsOpen = false;
            }

            e.Handled = true;
        }
    }

    // Was mit Dateien geschieht, die es am Ziel schon gibt – die Reihenfolge der Einträge ist die von ExistingRule.
    private void OnTransferRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransferRuleBox.SelectedIndex >= 0)
        {
            Settings.TransferRule = (NexusSSH.Core.Models.ExistingRule)TransferRuleBox.SelectedIndex;
            ShowMarks();
        }
    }

    // Knöpfe, die mehrfach gleich aussehen, heißen für eine Sprachausgabe nach dem, wozu sie gehören: „Probehören:
    // ein Transfer scheitert“, „Durchsuchen …: Editor für Dateien des Servers“. Sonst hießen sechs Knöpfe gleich.
    private void NameForScreenReaders()
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BrowseEditorButton, $"{L.Get("Browse")}: {L.Get("EditorLabel")}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BrowseCompareButton, $"{L.Get("Browse")}: {L.Get("CompareLabel")}");
        foreach (var row in Descendants(PageAlerts).OfType<StackPanel>())
        {
            if (row.Children is [CheckBox { Content: string what }, Button { Tag: string } preview])
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, $"{L.Get("SoundPreview")}: {what}");
            }
        }
    }

    private static IEnumerable<UIElement> Descendants(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            yield return child;
            if (child is Panel inner)
            {
                foreach (var below in Descendants(inner))
                {
                    yield return below;
                }
            }
        }
    }

    // Probehören: Welcher Ton gemeint ist, steht am Knopf (Tag = Name in SoundEvent).
    private void OnPreviewSound(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<SoundEvent>(name, out var sound))
        {
            Shell.PreviewSound(sound);
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedIndex >= 0)
        {
            Settings.Language = (AppLanguage)LanguageBox.SelectedIndex;
            ShowMarks();
        }
    }

    // Die Farbe wird nur gemerkt: WinUI liest sie beim Start, deshalb wirkt sie nach einem Neustart.
    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccentBox.SelectedItem is AccentColor accent)
        {
            Settings.Accent = accent.Id;
            ShowMarks();
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is TerminalTheme theme)
        {
            Shell.TerminalTheme = theme;
        }
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontBox.SelectedItem is string font)
        {
            Shell.TerminalFont = font;
        }
    }

    // Ein eingetippter Name: Was kein schlichter Name ist, wird zur Standardschrift – und steht dann auch so im Feld.
    private void OnFontSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        Shell.TerminalFont = args.Text;
        ShowFont();
        args.Handled = true;
    }

    private void OnUiZoomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsFinite(args.NewValue))
        {
            Shell.UiZoom = (int)Math.Round(args.NewValue);
        }

        // Auch nach einem Wert außerhalb der Grenzen: Im Feld steht, was gilt.
        sender.Value = Shell.UiZoom;
    }

    private void OnFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // Ein geleertes Feld meldet NaN; dann bleibt die Größe, wie sie ist.
        if (double.IsFinite(args.NewValue))
        {
            Shell.TerminalFontSize = (int)Math.Clamp(Math.Round(args.NewValue), TerminalThemes.MinFontSize, TerminalThemes.MaxFontSize);
        }
        else
        {
            sender.Value = Shell.TerminalFontSize;
        }
    }

    // Der Editor für „Datei bearbeiten“. Anführungszeichen vom Kopieren des Pfads im Explorer fallen weg.
    private void OnEditorPathChanged(object sender, TextChangedEventArgs e)
    {
        Settings.EditorPath = EditorBox.Text.Trim().Trim('"');
        ShowMarks();
    }

    private void OnComparePathChanged(object sender, TextChangedEventArgs e)
    {
        Settings.ComparePath = CompareBox.Text.Trim().Trim('"');
        ShowMarks();
    }

    private async void OnBrowseCompare(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        if (await picker.PickSingleFileAsync() is { } file)
        {
            CompareBox.Text = file.Path;
        }
    }

    private async void OnBrowseEditor(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        if (await picker.PickSingleFileAsync() is { } file)
        {
            EditorBox.Text = file.Path;
        }
    }

    // Die Datei wird nur gelesen und ist fremd: Was daraus wird, entscheidet der Leser in Core (ThemeImport).
    private async void OnImportThemeClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        string? text = null;
        try
        {
            var info = new FileInfo(file.Path);
            text = info.Length <= ThemeImport.MaxLength ? await File.ReadAllTextAsync(file.Path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            App.WriteCrashLog(exception);
        }

        ThemeHint.Text = Shell.ImportTheme(text, file.Path) is { } theme
            ? L.Get("ThemeImported", theme.Name)
            : L.Get("ThemeImportFailed");
    }

    private async void OnNewThemeClicked(object sender, RoutedEventArgs e) => await EditThemeAsync(Shell.NewTheme());

    private async void OnEditThemeClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.EditTheme() is { } editor)
        {
            await EditThemeAsync(editor);
        }
    }

    private async Task EditThemeAsync(ThemeEditorViewModel editor)
    {
        if (await App.Dialogs.EditThemeAsync(editor) is { } theme)
        {
            ThemeHint.Text = Shell.SaveTheme(theme) ? L.Get("ThemeSaved", theme.Name) : L.Get("ThemeSaveFailed");
        }
    }

    private void OnRemoveThemeClicked(object sender, RoutedEventArgs e)
    {
        var theme = Shell.TerminalTheme;
        if (Shell.RemoveTheme(theme))
        {
            ThemeHint.Text = L.Get("ThemeRemoved", theme.Name);
        }
    }

    private void SetBackdropDim(double value) => Shell.TerminalBackgroundDim = double.IsFinite(value) ? (int)Math.Round(value) : TerminalBackdrop.DefaultDim;

    // Nur der Pfad wird gemerkt; gelesen und geprüft wird die Datei, wenn das Terminal sie braucht.
    private async void OnPickBackdropClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (await picker.PickSingleFileAsync() is { } file)
        {
            Shell.TerminalBackgroundImage = file.Path;
        }
    }

    private void OnClearBackdropClicked(object sender, RoutedEventArgs e) => Shell.TerminalBackgroundImage = string.Empty;

    private void OnExportDataClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("data.export");

    private void OnImportDataClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("data.import");

    private void OnSaveDiagnosticsClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("app.diagnostics");

    private void OnShowTransferLogClicked(object sender, RoutedEventArgs e) => Shell.RunCommand("transfers.log");

    private void OnHelpClicked(object sender, RoutedEventArgs e) => Shell.IsHelpOpen = true;
}
