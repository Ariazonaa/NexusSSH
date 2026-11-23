using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core;
using NexusSSH.Core.Exchange;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

/// <summary>
/// Zeigt alle Dialoge der App. WinUI erlaubt je Fenster nur einen offenen ContentDialog – ein zweiter wirft.
/// Deshalb laufen alle Dialoge hier durch eine Warteschlange: Fragen zwei Sitzungen gleichzeitig nach einem
/// Passwort, kommt der zweite Dialog nach dem ersten.
/// </summary>
public sealed partial class DialogService(Func<XamlRoot?> xamlRoot) : IUserPrompts
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    // Rückfragen beim Verbinden entstehen für jedes Fenster neu: Zieht eine in ein anderes Fenster um, lässt sich
    // derselbe Dialog dort nicht noch einmal zeigen.
    public async Task<bool> ConfirmHostKeyAsync(ConnectionPrompt.HostKey prompt, CancellationToken cancellation)
        => await ShowAsync(() => new HostKeyDialog(prompt), cancellation) == ContentDialogResult.Primary;

    public async Task<SecretAnswer?> AskSecretAsync(ConnectionPrompt.Secret prompt, CancellationToken cancellation)
    {
        SecretDialog? shown = null;
        var result = await ShowAsync(() => shown = new SecretDialog(prompt), cancellation);
        return result == ContentDialogResult.Primary ? shown?.Answer : null;
    }

    /// <summary>
    /// Profil anlegen (<c>null</c>) oder ändern. <paramref name="profiles"/> sind alle Profile – aus ihnen wird der
    /// Sprung-Server gewählt. Liefert die Eingaben oder <c>null</c> bei Abbruch.
    /// </summary>
    public async Task<ServerProfile?> EditProfileAsync(ServerProfile? profile, IReadOnlyList<string> groups, IReadOnlyList<ServerProfile> profiles)
    {
        var editor = new ProfileEditorViewModel(profile, profiles, App.Shell.Identities, App.Shell.ReadSshConfig().Hosts, App.Shell.Themes, App.Shell.LocalShells);
        var dialog = new ProfileEditorDialog(editor, groups);
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? editor.ToProfile() : null;
    }

    /// <summary>Auswahl der Server aus <c>~/.ssh/config</c>, die als Profile übernommen werden. <c>null</c> bei Abbruch.</summary>
    /// <remarks><c>Linked</c>: Die Profile sollen mit ihrem Eintrag verknüpft bleiben und der Datei folgen.</remarks>
    public async Task<(IReadOnlyList<string> Aliases, bool Linked)?> ChooseSshConfigHostsAsync(SshConfigListing listing)
    {
        var import = new SshConfigImportViewModel(listing);
        var dialog = new SshConfigImportDialog(import);
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? (import.SelectedAliases, import.Linked) : null;
    }

    /// <summary>Auswahl der Sitzungen anderer Programme, die als Profile übernommen werden. <c>null</c> bei Abbruch.</summary>
    public async Task<IReadOnlyList<ForeignSession>?> ChooseForeignSessionsAsync(IReadOnlyList<ForeignSession> sessions, IReadOnlyList<ServerProfile> profiles)
    {
        var import = new SshConfigImportViewModel(sessions, profiles);
        var dialog = new SshConfigImportDialog(import);
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? import.SelectedSessions : null;
    }

    /// <summary>
    /// Eigenschaften eines Eintrags im Dateibrowser: Größe, Zeit, Rechte, Besitzer. Alles kommt vom Server und
    /// wird als reiner Text gezeigt. Rechte, Besitzer und Gruppe lassen sich ändern (wie <c>chmod</c> und
    /// <c>chown</c>), bei einem Ordner auch für alles darin: Dann kommt zurück, was geändert wurde, sonst <c>null</c>.
    /// </summary>
    /// <param name="measure">Rechnet die Größe eines Ordners aus (auf Wunsch).</param>
    /// <param name="learnTarget">
    /// Bei einem Link, dessen Liste das Ziel nicht nannte: holt es nach (der Eintrag mit <see cref="RemoteEntry.LinkTarget"/>,
    /// oder wie er war, wenn der Server es nicht sagt). Ohne sie steht dort „unbekannt“.
    /// </param>
    /// <param name="goToTarget">Wohin der Knopf „Zum Ziel“ führt: bekommt den Link mit seinem Ziel. Ohne sie gibt es den Knopf nicht.</param>
    public async Task<PropertyChange?> ShowPropertiesAsync(
        RemoteEntry entry,
        string path,
        Func<Task<string>>? measure = null,
        Func<Task<RemoteEntry>>? learnTarget = null,
        Func<RemoteEntry, Task>? goToTarget = null)
    {
        // Das Ziel steht im Link und kommt vom Server: reiner Text. Ein Link ins Leere sagt es dazu.
        static string TargetText(RemoteEntry link, string target)
            => link.IsDangling ? $"{Format.Visible(target)}\n{L.Get("LinkDangling")}" : Format.Visible(target);

        var rows = new List<(string Label, string Value)>
        {
            (L.Get("PropertyPath"), Format.Visible(path)),
            (L.Get("PropertyType"), entry.IsLink ? L.Get("TypeLink") : entry.IsSpecial ? L.Get("TypeSpecial") : entry.IsDirectory ? L.Get("TypeFolder") : L.Get("TypeFile")),
        };

        // Wohin ein Link zeigt: Die Liste nennt es meist selbst; sonst steht dort erst „…“, bis der Server geantwortet hat.
        var targetLabel = L.Get("PropertyTarget");
        if (entry.IsLink)
        {
            rows.Add((targetLabel, entry.LinkTarget is { Length: > 0 } listed
                ? TargetText(entry, listed)
                : learnTarget is null ? L.Get("LinkTargetUnknown") : "…"));
        }

        if (!entry.IsDirectory)
        {
            // Ab einem Kilobyte zusätzlich die genaue Zahl – darunter stünde zweimal dasselbe da.
            var size = Format.FileSize(entry.Size, isDirectory: false);
            rows.Add((L.Get("ColumnSize"), entry.Size < 1024 ? size : $"{size}  ({entry.Size.ToString("N0", L.Culture)} B)"));
        }

        rows.Add((L.Get("ColumnModified"), Format.DateAndTime(entry.Modified)));
        rows.Add((L.Get("PropertyPermissions"), Format.Permissions(entry.Permissions, entry.IsDirectory, entry.IsLink)));
        rows.Add((L.Get("PropertyOwner"), entry.Owner ?? string.Empty));
        rows.Add((L.Get("PropertyGroup"), entry.Group ?? string.Empty));

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8, MinWidth = 380, MaxWidth = 500 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // Was der Server nicht nennt, steht auch nicht da.
        TextBlock? targetText = null;
        foreach (var (label, value) in rows.Where(row => row.Value.Length > 0))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Foreground = secondary };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            FrameworkElement cell = text;
            if (entry.IsLink && label == targetLabel)
            {
                // Das Ziel kann Kilobytes lang sein (der Kern lässt bis 4096 Bytes zu), und ein ContentDialog rollt
                // nicht von selbst: Ohne Grenze schöbe es „Schließen“ aus dem Fenster. Der volle Text bleibt lesbar –
                // in einem Feld mit eigener Rollleiste (auch mit den Pfeiltasten) und auswählbar.
                targetText = text;
                cell = new ScrollViewer
                {
                    Content = text,
                    MaxHeight = 120,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    IsTabStop = true,
                };
                AutomationProperties.SetName(cell, label);
            }

            Grid.SetRow(name, grid.RowDefinitions.Count - 1);
            Grid.SetRow(cell, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(cell, 1);
            grid.Children.Add(name);
            grid.Children.Add(cell);
        }

        // Die Größe eines Ordners kennt SFTP nicht; der Server rechnet sie auf Wunsch aus (du). Das kann dauern –
        // deshalb ein Knopf und nicht von selbst.
        if (entry.IsDirectory && !entry.IsLink && measure is not null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = L.Get("ColumnSize"), Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            var measured = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var ask = new HyperlinkButton { Content = L.Get("FolderSizeAsk"), Padding = new Thickness(0) };
            AutomationProperties.SetName(ask, L.Get("FolderSizeAsk"));
            ask.Click += async (_, _) =>
            {
                ask.Visibility = Visibility.Collapsed;
                (measured.Text, measured.Visibility) = (L.Get("FolderSizeBusy"), Visibility.Visible);
                measured.Text = await measure();
            };
            foreach (var element in new FrameworkElement[] { name, ask, measured })
            {
                Grid.SetRow(element, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(element, ReferenceEquals(element, name) ? 0 : 1);
                grid.Children.Add(element);
            }
        }

        var dialog = new ContentDialog
        {
            Title = new TextBlock { Text = entry.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 },
            Content = grid,
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };

        // Ändern lässt sich nichts an einem Link: chmod und chown träfen dessen Ziel, nicht den Eintrag, der hier steht.
        if (entry.IsLink)
        {
            // „Zum Ziel“ gibt es erst, wenn das Ziel feststeht – nicht bei einem Link ins Leere (dort ist nichts, wohin man
            // ginge) und nicht bei einem Server, der es nie nennt: Ein Knopf, den es nie zu benutzen gibt, sähe nach Fehler aus.
            var shown = entry;
            void OfferGoTo()
            {
                if (goToTarget is not null && !shown.IsDangling && shown.LinkTarget is { Length: > 0 })
                {
                    dialog.SecondaryButtonText = L.Get("GoToTarget");
                }
            }

            OfferGoTo();

            // Der Server hat das Ziel in der Liste nicht genannt: Der Dialog fragt nach, er steht solange da.
            async Task LearnAsync()
            {
                try
                {
                    shown = await learnTarget!();
                }
                catch (Exception exception)
                {
                    App.WriteCrashLog(exception);
                }

                if (targetText is not null)
                {
                    targetText.Text = shown.LinkTarget is { Length: > 0 } learned ? TargetText(shown, learned) : L.Get("LinkTargetUnknown");
                }

                OfferGoTo();
            }

            if (learnTarget is not null && entry.LinkTarget is not { Length: > 0 })
            {
                _ = LearnAsync();
            }

            var closed = await ShowAsync(dialog, CancellationToken.None);
            if (closed == ContentDialogResult.Secondary && goToTarget is not null)
            {
                await goToTarget(shown);
            }

            return null;
        }

        TextBox Field(string header, string text, int maxLength, double width)
        {
            var box = new TextBox
            {
                Header = header,
                Text = text,
                MaxLength = maxLength,
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsSpellCheckEnabled = false,
            };
            AutomationProperties.SetName(box, header);
            return box;
        }

        var mode = Field(L.Get("ChangePermissionsLabel"), entry.Permissions is { } current ? Format.Octal(current) : string.Empty, 4, 150);
        mode.IsEnabled = entry.Permissions is not null;
        var owner = Field(L.Get("ChangeOwnerLabel"), entry.Owner ?? string.Empty, 64, 150);
        var group = Field(L.Get("ChangeGroupLabel"), entry.Group ?? string.Empty, 64, 150);
        var recursive = new CheckBox
        {
            Content = new TextBlock { Text = L.Get("ChangeRecursive"), TextWrapping = TextWrapping.Wrap },
            Visibility = entry.IsDirectory ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetName(recursive, L.Get("ChangeRecursive"));

        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        fields.Children.Add(mode);
        fields.Children.Add(owner);
        fields.Children.Add(group);
        foreach (var element in new FrameworkElement[] { fields, recursive })
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(element, 2);
            grid.Children.Add(element);
        }

        // Übernehmen geht erst, wenn die Eingaben taugen und etwas ändern würden.
        PropertyChange? Read() => PropertyChange.From(entry, mode.Text, owner.Text, group.Text, recursive.IsChecked == true);
        dialog.PrimaryButtonText = L.Get("ChangePermissions");
        dialog.IsPrimaryButtonEnabled = false;
        void Update() => dialog.IsPrimaryButtonEnabled = Read() is not null;
        mode.TextChanged += (_, _) => Update();
        owner.TextChanged += (_, _) => Update();
        group.TextChanged += (_, _) => Update();
        recursive.Checked += (_, _) => Update();
        recursive.Unchecked += (_, _) => Update();

        var result = await ShowAsync(dialog, CancellationToken.None);
        return result == ContentDialogResult.Primary ? Read() : null;
    }

    /// <summary>
    /// Rückfrage vor dem Import einer Export-Datei: was sie enthält und was davon übernommen wird. <c>null</c> bei
    /// Abbruch. Die Zahlen und Namen stammen aus der Datei und stehen als reiner Text da.
    /// </summary>
    public async Task<ImportChoice?> ChooseImportAsync(ImportPlan plan)
    {
        CheckBox Part(string text, bool available) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = available, IsEnabled = available };

        // Was nichts ändern würde, lässt sich auch nicht ankreuzen.
        var profiles = Part(L.Get("DataImportProfiles", plan.NewProfiles, plan.ChangedProfiles, plan.UnchangedProfiles), plan.NewProfiles + plan.ChangedProfiles > 0);
        var snippets = Part(L.Get("DataImportSnippets", plan.NewSnippets, plan.ChangedSnippets), plan.NewSnippets + plan.ChangedSnippets > 0);
        var themes = plan.NewThemes + plan.ChangedThemes;
        var settings = Part(
            themes > 0 ? $"{L.Get("DataImportSettings")} – {L.Get("DataImportThemes", plan.NewThemes, plan.ChangedThemes)}" : L.Get("DataImportSettings"),
            plan.Settings is not null || themes > 0);
        foreach (var box in new[] { profiles, snippets, settings })
        {
            AutomationProperties.SetName(box, ((TextBlock)box.Content).Text);
        }

        var content = new StackPanel { Spacing = 8, MinWidth = 380, MaxWidth = 480 };
        content.Children.Add(profiles);
        content.Children.Add(snippets);
        content.Children.Add(settings);
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var line in ShellViewModel.DescribeImport(plan))
        {
            content.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        }

        var dialog = new ContentDialog
        {
            Title = L.Get("DataImportTitle"),
            Content = content,
            PrimaryButtonText = L.Get("DataImportAction"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Update() => dialog.IsPrimaryButtonEnabled = profiles.IsChecked == true || snippets.IsChecked == true || settings.IsChecked == true;
        foreach (var box in new[] { profiles, snippets, settings })
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
        }

        Update();
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? new ImportChoice(profiles.IsChecked == true, snippets.IsChecked == true, settings.IsChecked == true)
            : null;
    }

    /// <summary>Eine Mitteilung, die nur bestätigt werden kann.</summary>
    public async Task ShowMessageAsync(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog, CancellationToken.None);
    }

    public Task<bool> ConfirmDeleteAsync(ServerProfile profile)
        => ConfirmAsync(L.Get("DeleteProfileTitle", profile.Name), L.Get("DeleteProfileText"), L.Get("Delete"));

    /// <summary>Ja/Nein-Frage vor etwas, das sich nicht zurücknehmen lässt. „Abbrechen“ ist die Vorgabe.</summary>
    /// <summary>
    /// Am Ziel eines Transfers gibt es schon etwas mit demselben Namen: Der Benutzer wählt, was geschieht – einmal
    /// für alles, was er gerade überträgt. <c>null</c> bei Abbruch. Hat er sich eine Regel gemerkt, gilt sie ohne
    /// Frage; „merken“ im Dialog schreibt sie in die Einstellungen.
    /// </summary>
    public async Task<ExistingRule?> AskExistingAsync(string text, NexusSSH.Core.Settings.AppSettings settings)
    {
        if (settings.TransferRule != ExistingRule.Ask)
        {
            return settings.TransferRule;
        }

        var choices = new (ExistingRule Rule, RadioButton Button)[]
        {
            (ExistingRule.Overwrite, new RadioButton { Content = L.Get("ExistingOverwrite"), GroupName = "existing", IsChecked = true }),
            (ExistingRule.SkipSame, new RadioButton { Content = L.Get("ExistingSkipSame"), GroupName = "existing" }),
            (ExistingRule.Newer, new RadioButton { Content = L.Get("ExistingNewer"), GroupName = "existing" }),
            (ExistingRule.Rename, new RadioButton { Content = L.Get("ExistingRename"), GroupName = "existing" }),
        };
        var remember = new CheckBox { Content = L.Get("ExistingRemember") };
        var panel = new StackPanel { Width = 440, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var (_, button) in choices)
        {
            panel.Children.Add(button);
        }

        panel.Children.Add(new TextBlock
        {
            Text = L.Get("ExistingHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(remember);

        var dialog = new ContentDialog
        {
            Title = L.Get("OverwriteTitle"),
            Content = panel,
            PrimaryButtonText = L.Get("ExistingContinue"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary)
        {
            return null;
        }

        var rule = choices.FirstOrDefault(choice => choice.Button.IsChecked == true).Rule;
        rule = rule == ExistingRule.Ask ? ExistingRule.Overwrite : rule;
        if (remember.IsChecked == true)
        {
            settings.TransferRule = rule;
        }

        return rule;
    }

    public async Task<bool> ConfirmAsync(string title, string text, string confirm)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = confirm,
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Fragt, ob ein Befehl eines KI-Programms laufen darf. Der Befehl steht ganz da, jedes Zeichen sichtbar – auch
    /// eines, das in einem Terminal etwas verbergen würde. Vorgabe ist „Ablehnen“; schließt die Frage ohne Antwort, heißt
    /// das „nein“. Die Frist läuft außerhalb des Threads der Oberfläche, deshalb der Umweg über die Warteschlange. Was von
    /// ihr bleibt, steht am Knopf „Ablehnen“ und als Balken; „Immer erlauben“ gibt genau diesen Text frei.
    /// </summary>
    public async Task<CommandAnswer> ConfirmCommandAsync(CommandQuestion question, CancellationToken cancellation)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var closing = new CancellationTokenSource();
        var open = true;
        await using var forward = cancellation.Register(() => queue.TryEnqueue(() =>
        {
            if (open)
            {
                closing.Cancel();
            }
        }));

        var total = Math.Max(1, (question.Until - DateTimeOffset.UtcNow).TotalSeconds);
        var left = new ProgressBar { Maximum = total, Value = total };
        AutomationProperties.SetName(left, L.Get("McpCommandTimeLeft"));
        var dialog = new ContentDialog
        {
            Title = L.Get("McpCommandTitle", question.Server),
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = question.Plugin is { } plugin ? L.Get("PluginCommandHint", plugin) : L.Get(question.InTerminal ? "McpCommandHintTerminal" : "McpCommandHint"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = string.Join('\n', question.Command.Split('\n').Select(Format.Visible)),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    },
                    new TextBlock
                    {
                        Text = question.Plugin is null ? L.Get("McpCommandAlwaysHint") : L.Get("PluginCommandOnce"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    left,
                },
            },
            PrimaryButtonText = L.Get("McpCommandRun"),

            // Ein Plugin bekommt keine dauerhafte Freigabe: Jeder seiner Befehle wird gefragt.
            SecondaryButtonText = question.Plugin is null ? L.Get("McpCommandAlways") : string.Empty,
            DefaultButton = ContentDialogButton.Close,
        };

        // Die Frist selbst hält der Aufrufer; hier wird nur gezeigt, was von ihr bleibt.
        void Tick()
        {
            var remaining = question.Until - DateTimeOffset.UtcNow;
            remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            dialog.CloseButtonText = L.Get("McpCommandDeclineIn", remaining.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture));
            left.Value = Math.Min(total, remaining.TotalSeconds);
        }

        Tick();
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => Tick();
        timer.Start();
        try
        {
            return await ShowAsync(dialog, closing.Token) switch
            {
                ContentDialogResult.Primary => CommandAnswer.Run,
                ContentDialogResult.Secondary => CommandAnswer.Always,
                _ => CommandAnswer.Decline,
            };
        }
        catch (OperationCanceledException)
        {
            return CommandAnswer.Decline;
        }
        finally
        {
            timer.Stop();
            open = false;
        }
    }

    /// <summary>
    /// Fragt nach dem Dateinamen und der Passphrase für ein neues Schlüsselpaar. <c>null</c> bei Abbruch. Die
    /// Passphrase steht zweimal da: Ein Tippfehler darin sperrte den Schlüssel für immer.
    /// </summary>
    public async Task<KeyRequest?> AskKeyAsync(ServerProfile profile)
    {
        var name = new TextBox { Header = L.Get("KeyNameLabel"), Text = KeyRequest.SuggestedName(profile), IsSpellCheckEnabled = false };
        var passphrase = new PasswordBox { Header = L.Get("KeyPassphraseLabel") };
        var repeat = new PasswordBox { Header = L.Get("KeyPassphraseRepeatLabel") };
        var hint = new TextBlock
        {
            Text = L.Get("KeyHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var content = new StackPanel { Width = 400, Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(passphrase);
        content.Children.Add(repeat);

        // Hat das Profil eine Identität, steht zur Wahl, wem der neue Schlüssel gilt. Vorgewählt ist das Profil
        // allein: Hinterlegt wird der Schlüssel nur auf diesem einen Server.
        var stored = App.Shell.Profiles.FirstOrDefault(candidate => candidate.Id == profile.Id) ?? profile;
        CheckBox? forIdentity = null;
        if (App.Shell.IdentityOf(stored) is { } identity)
        {
            forIdentity = new CheckBox
            {
                Content = new TextBlock { Text = L.Get("KeyForIdentity", identity.Name, App.Shell.ProfilesUsing(identity)), TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(forIdentity, L.Get("KeyForIdentity", identity.Name, App.Shell.ProfilesUsing(identity)));
            content.Children.Add(forIdentity);
        }

        content.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = L.Get("KeyTitle", profile.Name),
            Content = content,
            PrimaryButtonText = L.Get("KeyAction"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Check() => dialog.IsPrimaryButtonEnabled = KeyRequest.IsValidName(name.Text.Trim()) && passphrase.Password == repeat.Password;
        name.TextChanged += (_, _) => Check();
        passphrase.PasswordChanged += (_, _) => Check();
        repeat.PasswordChanged += (_, _) => Check();
        Check();
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? new KeyRequest(name.Text.Trim(), passphrase.Password.Length > 0 ? passphrase.Password : null, forIdentity?.IsChecked == true)
            : null;
    }

    /// <summary>
    /// „Hinterlegte Schlüssel“: die Dateien <c>authorized_keys</c> des Kontos mit ihren Schlüsseln, je einer mit
    /// „Entfernen“ (die Rückfrage steht in der Zeile selbst – ein zweiter Dialog geht über einem offenen nicht), unter jeder
    /// Datei „Eigenen Schlüssel hinzufügen“. Nach jedem Schritt liest der Dialog neu; was geschieht, entscheidet Core.
    /// </summary>
    public async Task ShowAuthorizedKeysAsync(AuthorizedKeysViewModel keys)
    {
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var caution = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var list = new StackPanel { Spacing = 14 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = caution, Visibility = Visibility.Collapsed };
        // Breiter als 500 geht nicht: Ein ContentDialog ist höchstens 548 breit, samt Rand.
        var content = new StackPanel { Width = 480, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = L.Get("AuthorizedIntro"), TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        content.Children.Add(status);
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        void Say(string? text)
        {
            status.Text = text ?? string.Empty;
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        TextBlock Line(string text, bool mono = false, Microsoft.UI.Xaml.Media.Brush? brush = null)
        {
            var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = caption };
            if (mono)
            {
                line.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
            }

            if (brush is not null)
            {
                line.Foreground = brush;
            }

            return line;
        }

        async Task Run(Func<Task<string?>> step)
        {
            list.IsHitTestVisible = false;
            Say(L.Get("AuthorizedLoading"));
            var failure = await step();
            Render();
            Say(failure ?? (keys.Error.Length > 0 ? keys.Error : null));
            list.IsHitTestVisible = true;
        }

        UIElement Row(AuthorizedKeyItem item)
        {
            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            info.Children.Add(Line(item.Details, mono: item.CanRemove, brush: secondary));
            if (item.LocalName.Length > 0)
            {
                info.Children.Add(Line(item.IsProfileKey ? $"{L.Get("AuthorizedLocal", item.LocalName)} · {L.Get("AuthorizedProfileKey")}" : L.Get("AuthorizedLocal", item.LocalName), brush: secondary));
            }

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(info);
            if (!item.CanRemove)
            {
                return row;
            }

            var remove = new Button { Content = L.Get("AuthorizedRemove"), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(remove, $"{L.Get("AuthorizedRemove")}: {item.Title}");
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            // Die Rückfrage erscheint unter der Zeile: deutlicher, wenn es der eigene oder der letzte Schlüssel ist.
            var confirm = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            confirm.Children.Add(new TextBlock { Text = keys.RemoveWarning(item), TextWrapping = TextWrapping.Wrap, Foreground = caution });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var yes = new Button { Content = L.Get("AuthorizedRemoveYes"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var no = new Button { Content = L.Get("Cancel") };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            confirm.Children.Add(buttons);
            Grid.SetRow(confirm, 1);
            Grid.SetColumnSpan(confirm, 2);
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.Children.Add(confirm);

            remove.Click += (_, _) =>
            {
                remove.IsEnabled = false;
                confirm.Visibility = Visibility.Visible;
                no.Focus(FocusState.Programmatic);
            };
            no.Click += (_, _) =>
            {
                confirm.Visibility = Visibility.Collapsed;
                remove.IsEnabled = true;
                remove.Focus(FocusState.Programmatic);
            };
            yes.Click += async (_, _) => await Run(() => keys.RemoveAsync(item));
            return row;
        }

        void Render()
        {
            list.Children.Clear();
            foreach (var file in keys.Files)
            {
                var head = new StackPanel { Spacing = 2 };
                head.Children.Add(new TextBlock { Text = file.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (file.Note.Length > 0)
                {
                    head.Children.Add(Line(file.Note, brush: secondary));
                }

                list.Children.Add(head);
                foreach (var item in file.Keys)
                {
                    list.Children.Add(Row(item));
                }

                if (file.File.Error.Length == 0)
                {
                    list.Children.Add(Adding(file));
                }
            }
        }

        // Einen eigenen Schlüssel an diese Datei hängen – nur die, die dort noch nicht stehen.
        UIElement Adding(AuthorizedKeyFileItem file)
        {
            var add = new StackPanel { Spacing = 6 };
            add.Children.Add(Line(L.Get("AuthorizedAddHeader"), brush: secondary));
            if (file.Addable.Count == 0)
            {
                add.Children.Add(Line(L.Get("AuthorizedNothingToAdd"), brush: secondary));
                return add;
            }

            var names = file.Addable.Select(key => key.Comment.Length > 0 ? $"{key.Name} · {key.Comment}" : key.Name).ToList();
            var choice = new ComboBox { ItemsSource = names, SelectedIndex = 0, MinWidth = 280, MaxWidth = 360 };
            AutomationProperties.SetName(choice, $"{L.Get("AuthorizedAddHeader")}: {file.Title}");
            var button = new Button { Content = L.Get("AuthorizedAdd") };
            AutomationProperties.SetName(button, $"{L.Get("AuthorizedAdd")}: {file.Title}");
            button.Click += async (_, _) =>
            {
                if (choice.SelectedIndex is >= 0 and var index && index < file.Addable.Count)
                {
                    var key = file.Addable[index];
                    await Run(() => keys.AddAsync(file, key));
                }
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(choice);
            line.Children.Add(button);
            add.Children.Add(line);
            return add;
        }

        var dialog = new ContentDialog
        {
            Title = L.Get("AuthorizedTitle", keys.ServerName),
            Content = content,
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Opened += async (_, _) => await Run(async () =>
        {
            await keys.LoadAsync();
            return null;
        });

        await ShowAsync(dialog, CancellationToken.None);
    }

    /// <summary>
    /// Die Vorschau eines Erweiterungspakets vor dem Installieren: wer es gemacht hat, was drin ist – die Befehle mit
    /// ihrem ganzen Text –, und ob es ein installiertes ersetzt. Alles darin kommt von außen und ist schon geprüft.
    /// </summary>
    public async Task<bool> ConfirmExtensionAsync(NexusSSH.Core.Extensions.ExtensionPackage package, NexusSSH.Core.Extensions.ExtensionPackage? installed)
    {
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var mono = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
        var content = new StackPanel { Width = 480, Spacing = 8 };
        var by = package.Author.Length > 0 ? L.Get("ExtensionTitleBy", package.Name, package.Version, package.Author) : L.Get("ExtensionTitle", package.Name, package.Version);
        content.Children.Add(new TextBlock { Text = by, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (package.Description.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = package.Description, TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(new TextBlock { Text = package.Summary, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        if (installed is not null)
        {
            content.Children.Add(new TextBlock { Text = L.Get("ExtensionReplaces", installed.Version), TextWrapping = TextWrapping.Wrap });
        }

        var details = new StackPanel { Spacing = 4 };
        if (package.Themes.Count > 0)
        {
            details.Children.Add(new TextBlock { Text = $"{L.Get("ExtensionThemes")}: {string.Join(", ", package.Themes.Select(theme => theme.Name))}", TextWrapping = TextWrapping.Wrap });
        }

        foreach (var snippet in package.Snippets)
        {
            details.Children.Add(new TextBlock { Text = $"{snippet.Name}{(snippet.Run ? " ⏎" : string.Empty)}", Margin = new Thickness(0, 4, 0, 0) });
            details.Children.Add(new TextBlock { Text = string.Join('\n', snippet.Text.Split('\n').Select(Format.Visible)), FontFamily = mono, Foreground = secondary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        if (package.Highlights.Count > 0)
        {
            details.Children.Add(new TextBlock { Text = $"{L.Get("ExtensionHighlights")}: {string.Join(", ", package.Highlights.Select(rule => $"{rule.Color} {rule.Pattern}"))}", TextWrapping = TextWrapping.Wrap });
        }

        if (package.Completions.Count > 0)
        {
            details.Children.Add(new TextBlock { Text = $"{L.Get("ExtensionCompletions")}: {string.Join(", ", package.Completions.Select(table => table.Command))}", TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(new ScrollViewer { Content = details, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock { Text = L.Get("ExtensionFromOutside"), Foreground = secondary, TextWrapping = TextWrapping.Wrap });

        var dialog = new ContentDialog
        {
            Title = L.Get("ExtensionPreviewTitle"),
            Content = content,
            PrimaryButtonText = installed is null ? L.Get("ExtensionInstall") : L.Get("ExtensionReplace"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Die Vorschau eines Plugins vor dem Installieren: wer es gemacht hat, seine Befehle, seine Prüfsumme, und die
    /// Rechte, die es erbittet – jedes zum Anhaken, von Haus aus keines. Liefert die gewährten Rechte, <c>null</c> bei Abbruch.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmPluginAsync(NexusSSH.Core.Plugins.PluginManifest manifest, PluginInspection inspection, NexusSSH.Core.Plugins.InstalledPlugin? installed)
    {
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var content = new StackPanel { Width = 480, Spacing = 8 };
        var by = manifest.Author.Length > 0 ? L.Get("ExtensionTitleBy", manifest.Name, manifest.Version, manifest.Author) : L.Get("ExtensionTitle", manifest.Name, manifest.Version);
        content.Children.Add(new TextBlock { Text = by, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (manifest.Description.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = manifest.Description, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var line in PluginLines(manifest))
        {
            content.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        }

        if (installed is not null)
        {
            content.Children.Add(new TextBlock { Text = L.Get("ExtensionReplaces", installed.Manifest.Version), TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(new TextBlock { Text = L.Get("PluginBase"), Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        var boxes = new List<(string Permission, CheckBox Box)>();
        content.Children.Add(new TextBlock { Text = manifest.Permissions.Count > 0 ? L.Get("PluginPermissionsAsked") : L.Get("PluginNoPermissionsAsked"), TextWrapping = TextWrapping.Wrap });
        foreach (var permission in manifest.Permissions)
        {
            // Von Haus aus nichts gewährt – auch nicht beim Ersetzen: Neue Rechte gibt es nur angehakt.
            var box = new CheckBox { Content = ShellViewModel.PermissionName(permission), IsChecked = installed?.Has(permission) == true };
            boxes.Add((permission, box));
            content.Children.Add(box);
        }

        content.Children.Add(new TextBlock { Text = L.Get("PluginCode"), Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = L.Get("PluginSha", inspection.Sha256), Foreground = secondary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") });

        var dialog = new ContentDialog
        {
            Title = L.Get("PluginPreviewTitle"),
            Content = new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = installed is null ? L.Get("ExtensionInstall") : L.Get("ExtensionReplace"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? [.. boxes.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Permission)]
            : null;
    }

    /// <summary>Was ein Plugin tut, in Zeilen: seine Befehle, die Ereignisse, auf die es hört, die Muster, deren Zeilen es sieht.</summary>
    public static IEnumerable<string> PluginLines(NexusSSH.Core.Plugins.PluginManifest manifest)
    {
        if (manifest.Commands.Count > 0)
        {
            yield return L.Get("PluginCommandsList", string.Join(", ", manifest.Commands.Select(command => command.Title)));
        }

        if (manifest.Events.Count > 0)
        {
            yield return L.Get("PluginEventsList", ShellViewModel.EventNames(manifest.Events));
        }

        if (manifest.Watch.Count > 0)
        {
            yield return L.Get("PluginWatchList", ShellViewModel.PatternTexts(manifest.Watch));
        }
    }

    /// <summary>Was ein Plugin zeigen will: Text und Tabelle, beides fremd und schon gesäubert.</summary>
    public async Task ShowPluginViewAsync(string plugin, NexusSSH.Core.Plugins.PluginView view)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
        if (view.Text.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = view.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        if (view.Rows.Count > 0)
        {
            var table = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
            var columns = Math.Max(view.Columns.Count, view.Rows.Max(row => row.Count));
            for (var column = 0; column < columns; column++)
            {
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var line = 0;
            void Add(IReadOnlyList<string> cells, bool head)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var column = 0; column < cells.Count && column < columns; column++)
                {
                    var cell = new TextBlock { Text = cells[column], IsTextSelectionEnabled = true };
                    if (head)
                    {
                        cell.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    }

                    Grid.SetRow(cell, line);
                    Grid.SetColumn(cell, column);
                    table.Children.Add(cell);
                }

                line++;
            }

            if (view.Columns.Count > 0)
            {
                Add(view.Columns, head: true);
            }

            foreach (var row in view.Rows)
            {
                Add(row, head: false);
            }

            content.Children.Add(new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto });
        }

        var dialog = new ContentDialog
        {
            Title = view.Title.Length > 0 ? L.Get("PluginViewTitle", plugin, view.Title) : plugin,
            Content = new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog, CancellationToken.None);
    }

    /// <summary>„Als Paket speichern“: Name, Autor und was hinein soll. <c>null</c> bei Abbruch.</summary>
    public async Task<(string Name, string Author, bool Themes, bool Snippets, bool Highlights)?> AskOwnPackageAsync()
    {
        var name = new TextBox { Header = L.Get("ExtensionName"), Text = L.Get("ExtensionNameSuggestion"), IsSpellCheckEnabled = false };
        var author = new TextBox { Header = L.Get("ExtensionAuthor"), IsSpellCheckEnabled = false };
        var themes = new CheckBox { Content = L.Get("ExtensionThemes"), IsChecked = App.Shell.Settings.CustomThemes.Count > 0, IsEnabled = App.Shell.Settings.CustomThemes.Count > 0 };
        var snippets = new CheckBox { Content = L.Get("ExtensionSnippets"), IsChecked = App.Shell.Settings.Snippets.Count > 0, IsEnabled = App.Shell.Settings.Snippets.Count > 0 };
        var highlights = new CheckBox { Content = L.Get("ExtensionHighlights"), IsChecked = App.Shell.HighlightRules.Count > 0, IsEnabled = App.Shell.HighlightRules.Count > 0 };
        var content = new StackPanel { Width = 400, Spacing = 8 };
        content.Children.Add(name);
        content.Children.Add(author);
        content.Children.Add(themes);
        content.Children.Add(snippets);
        content.Children.Add(highlights);
        content.Children.Add(new TextBlock
        {
            Text = L.Get("ExtensionSaveHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            Title = L.Get("ExtensionSaveOwn"),
            Content = content,
            PrimaryButtonText = L.Get("ExtensionSaveAction"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Check() => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 && (themes.IsChecked == true || snippets.IsChecked == true || highlights.IsChecked == true);
        name.TextChanged += (_, _) => Check();
        themes.Click += (_, _) => Check();
        snippets.Click += (_, _) => Check();
        highlights.Click += (_, _) => Check();
        Check();

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? (name.Text.Trim(), author.Text.Trim(), themes.IsChecked == true, snippets.IsChecked == true, highlights.IsChecked == true)
            : null;
    }

    /// <summary>
    /// Ein gespeicherter Befehl aus einem Erweiterungspaket soll ins Terminal: erst fragen, mit dem ganzen Text, jedes
    /// Zeichen sichtbar. Vorgabe ist „Abbrechen“; „Immer erlauben“ gibt genau diesen Text aus diesem Paket frei.
    /// </summary>
    public async Task<CommandAnswer> ConfirmPackCommandAsync(string package, string name, string command)
    {
        var dialog = new ContentDialog
        {
            Title = L.Get("ExtensionCommandTitle", package),
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 460,
                Children =
                {
                    new TextBlock { Text = L.Get("ExtensionCommandText", name), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = string.Join('\n', command.Split('\n').Select(Format.Visible)),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    },
                },
            },
            PrimaryButtonText = L.Get("ExtensionCommandRun"),
            SecondaryButtonText = L.Get("ExtensionCommandAlways"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) switch
        {
            ContentDialogResult.Primary => CommandAnswer.Run,
            ContentDialogResult.Secondary => CommandAnswer.Always,
            _ => CommandAnswer.Decline,
        };
    }

    /// <summary>
    /// Fragt nach dem Tastenkürzel für einen Befehl: eine Taste aus der Liste (oder keine) und ob Umschalt
    /// dazugehört – Strg gehört immer dazu. Was sich nicht belegen lässt, sagt der Dialog gleich
    /// (<paramref name="problem"/>). Liefert <c>false</c> bei Abbruch.
    /// </summary>
    public async Task<(bool Chosen, KeyChord? Chord)> AskShortcutAsync(ShortcutRow row, Func<KeyChord?, string?> problem)
    {
        var names = new List<string> { L.Get("ShortcutNone") };
        names.AddRange(KeyChord.Codes.Select(KeyChord.KeyName));
        var key = new ComboBox
        {
            Header = L.Get("ShortcutKey"),
            ItemsSource = names,
            SelectedIndex = row.Chord is { } current ? KeyChord.Codes.ToList().IndexOf(current.Code) + 1 : 0,
            Width = 200,
        };
        var shift = new CheckBox { Content = L.Get("ShortcutWithShift"), IsChecked = row.Chord?.Shift ?? true };
        var preview = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        // Das Kürzel lässt sich auch einfach drücken: Hat dieses Feld den Fokus, wählt Strg (+ Umschalt) + Taste die
        // Taste in der Liste und setzt das Kästchen. Ohne Strg bleiben die Tasten, was sie sind – Tab geht weiter,
        // Esc schließt den Dialog.
        var press = new TextBox { IsReadOnly = true, PlaceholderText = L.Get("ShortcutPress"), IsSpellCheckEnabled = false };
        AutomationProperties.SetName(press, L.Get("ShortcutPress"));
        press.PreviewKeyDown += (_, e) =>
        {
            var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var index = control ? KeyChord.Codes.ToList().FindIndex(code => MainWindow.ToVirtualKey(code) == e.Key) : -1;
            if (index >= 0)
            {
                shift.IsChecked = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                key.SelectedIndex = index + 1;
                e.Handled = true;
            }
        };

        var content = new StackPanel { Width = 400, Spacing = 12 };
        content.Children.Add(press);
        content.Children.Add(key);
        content.Children.Add(shift);
        content.Children.Add(preview);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = row.Title,
            Content = content,
            PrimaryButtonText = L.Get("Save"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        KeyChord? Chosen() => key.SelectedIndex > 0 ? new KeyChord(KeyChord.Codes[key.SelectedIndex - 1], shift.IsChecked == true) : null;
        void Check()
        {
            var chord = Chosen();
            preview.Text = chord?.Display ?? L.Get("ShortcutNone");
            error.Text = problem(chord) ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = error.Text.Length == 0;
        }

        dialog.Opened += (_, _) => press.Focus(FocusState.Programmatic);
        key.SelectionChanged += (_, _) => Check();
        shift.Checked += (_, _) => Check();
        shift.Unchecked += (_, _) => Check();
        Check();

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? (true, Chosen()) : (false, null);
    }

    /// <summary>Legt eine Identität an (<c>null</c>) oder ändert sie. Liefert die Eingaben oder <c>null</c> bei Abbruch.</summary>
    public async Task<Identity?> EditIdentityAsync(Identity? identity)
    {
        var name = new TextBox { Header = L.Get("IdentityName"), Text = identity?.Name ?? string.Empty, IsSpellCheckEnabled = false };
        var user = new TextBox { Header = L.Get("ProfileUser"), Text = identity?.User ?? string.Empty, IsSpellCheckEnabled = false, PlaceholderText = "root" };
        var key = new TextBox { Header = L.Get("ProfileKeyFile"), Text = identity?.KeyPath ?? string.Empty, IsSpellCheckEnabled = false, PlaceholderText = L.Get("ProfileKeyFilePlaceholder") };
        var browse = new Button { Content = L.Get("Browse"), VerticalAlignment = VerticalAlignment.Bottom };
        var agent = new CheckBox { Content = L.Get("ProfileUseAgent"), IsChecked = identity?.UseAgent ?? true };

        var keyRow = new Grid { ColumnSpacing = 8 };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        keyRow.Children.Add(key);
        keyRow.Children.Add(browse);

        var content = new StackPanel { Width = 400, Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(user);
        content.Children.Add(keyRow);
        content.Children.Add(agent);

        var dialog = new ContentDialog
        {
            Title = identity is null ? L.Get("IdentityAdd") : L.Get("IdentityEditTitle", identity.Name),
            Content = content,
            PrimaryButtonText = L.Get("Save"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        void Check() => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 && Identity.IsValidUser(user.Text.Trim());
        name.TextChanged += (_, _) => Check();
        user.TextChanged += (_, _) => Check();
        Check();
        browse.Click += async (_, _) =>
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(dialog.XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Unspecified,
            };
            picker.FileTypeFilter.Add("*");
            if (await picker.PickSingleFileAsync() is { } file)
            {
                key.Text = file.Path;
            }
        };
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? new Identity(identity?.Id ?? string.Empty, name.Text, user.Text, key.Text, agent.IsChecked == true)
            : null;
    }

    /// <summary>
    /// Fragt, in welche Richtung ein Ordner abgeglichen wird: der lokale zum Server oder umgekehrt – und ob nach
    /// Inhalt verglichen wird (Prüfsummen) statt nach Größe und Zeit. <c>null</c> bei Abbruch.
    /// </summary>
    /// <param name="excludes">Die Ausschlüsse aus den Einstellungen – für diesen einen Abgleich änderbar.</param>
    /// <returns>Dazu der Name, unter dem der Abgleich gemerkt werden soll – <c>null</c>, wenn keiner (oder kein
    /// tauglicher) eingetragen ist.</returns>
    public async Task<(SyncDirection Direction, bool ByContent, IReadOnlyList<string> Exclude, string? Name)?> AskSyncDirectionAsync(string localFolder, string remoteFolder, IReadOnlyList<string> excludes)
    {
        var byContent = new CheckBox { Content = L.Get("SyncByContent") };
        var hint = new TextBlock
        {
            Text = L.Get("SyncByContentHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var content = new StackPanel { MaxWidth = 440, Spacing = 8 };
        content.Children.Add(new TextBlock { Text = L.Get("SyncDirectionText", localFolder, remoteFolder), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(byContent);
        content.Children.Add(hint);

        // Ausschlüsse nur für diesen Abgleich: ein Muster je Zeile, vorbelegt aus den Einstellungen.
        var exclude = new TextBox
        {
            Header = L.Get("SyncExclude"),
            Text = string.Join((char)13, excludes),
            AcceptsReturn = true,
            IsSpellCheckEnabled = false,
            Height = 96,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(exclude, ScrollBarVisibility.Auto);
        content.Children.Add(exclude);
        content.Children.Add(new TextBlock
        {
            Text = L.Get("SyncExcludeHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        // Mit einem Namen steht der Abgleich danach auf der Startseite – leer bleibt alles wie bisher.
        var remember = new TextBox { Header = L.Get("SyncRemember"), MaxLength = NexusSSH.Core.Settings.SavedSync.MaxNameLength };
        content.Children.Add(remember);
        content.Children.Add(new TextBlock
        {
            Text = L.Get("SyncRememberHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = L.Get("SyncTitle"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L.Get("SyncUpload"),
            SecondaryButtonText = L.Get("SyncDownload"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) switch
        {
            ContentDialogResult.Primary => (SyncDirection.Upload, byContent.IsChecked == true, Patterns(), Name()),
            ContentDialogResult.Secondary => (SyncDirection.Download, byContent.IsChecked == true, Patterns(), Name()),
            _ => null,
        };

        string? Name() => NexusSSH.Core.Settings.SavedSync.IsValidName(remember.Text) ? remember.Text.Trim() : null;

        // Das Textfeld von WinUI trennt Zeilen mit einem Wagenrücklauf.
        List<string> Patterns() => NexusSSH.Core.Services.TransferExcludes.Parse(exclude.Text.Replace((char)13, (char)10));
    }

    /// <summary>
    /// Zeigt, was ein Abgleich übertrüge – die Zusammenfassung und die ersten Dateien –, und fragt, ob er beginnen
    /// soll. <c>null</c> bei Abbruch; sonst, ob vorher gelöscht werden soll, was nur am Ziel liegt.
    /// </summary>
    public async Task<bool?> ConfirmSyncAsync(SyncPlan plan)
    {
        const int Shown = 200;
        var lines = plan.Items.Take(Shown).Select(item => item.IsNew ? $"{Format.Visible(item.RelativePath)}  ({L.Get("SyncNew")})" : Format.Visible(item.RelativePath)).ToList();
        if (plan.Items.Count > Shown)
        {
            lines.Add(L.Get("SyncMore", plan.Items.Count - Shown));
        }

        var content = new StackPanel { Width = 460, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = SftpBrowserViewModel.Describe(plan), TextWrapping = TextWrapping.Wrap });
        if (lines.Count > 0)
        {
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock
                {
                    Text = string.Join('\n', lines),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                },
            });
        }

        // Was nur am Ziel liegt, bleibt dort – außer das Kästchen ist gesetzt. Es ist nie vorgewählt, und was
        // gelöscht würde, steht darunter.
        CheckBox? delete = null;
        if (plan.Extra.Count > 0)
        {
            var text = plan.Direction == SyncDirection.Upload ? L.Get("SyncDeleteRemote", plan.Extra.Count) : L.Get("SyncDeleteLocal", plan.Extra.Count);
            delete = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
            AutomationProperties.SetName(delete, text);
            content.Children.Add(delete);

            var extra = plan.Extra.Take(Shown).Select(item => Format.Visible(item.RelativePath)).ToList();
            if (plan.Extra.Count > Shown)
            {
                extra.Add(L.Get("SyncMore", plan.Extra.Count - Shown));
            }

            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 120,
                Content = new TextBlock
                {
                    Text = string.Join('\n', extra),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    IsTextSelectionEnabled = true,
                },
            });
        }

        var dialog = new ContentDialog
        {
            Title = L.Get("SyncTitle"),
            Content = content,
            PrimaryButtonText = plan.Items.Count > 0 || delete is not null ? L.Get("SyncStart") : string.Empty,
            CloseButtonText = plan.Items.Count > 0 || delete is not null ? L.Get("Cancel") : L.Get("Close"),
            DefaultButton = plan.Items.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
            IsPrimaryButtonEnabled = plan.Items.Count > 0,
        };
        if (delete is not null)
        {
            // Ohne etwas zu übertragen ist „Übertragen“ nur mit dem Kästchen sinnvoll.
            void Check() => dialog.IsPrimaryButtonEnabled = plan.Items.Count > 0 || delete.IsChecked == true;
            delete.Checked += (_, _) => Check();
            delete.Unchecked += (_, _) => Check();
        }

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? delete?.IsChecked == true : null;
    }

    /// <summary>Fragt einen kurzen Text ab (Name eines Ordners, neuer Dateiname). <c>null</c> bei Abbruch.</summary>
    public async Task<string?> AskTextAsync(string title, string label, string initial, string confirm, Func<string, bool> isValid)
    {
        var box = new TextBox { Header = label, Text = initial, Width = 360, IsSpellCheckEnabled = false };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = confirm,
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = isValid(initial.Trim()),
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = isValid(box.Text.Trim());
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);

            // Beim Umbenennen den Namen ohne Endung vorwählen, wie im Explorer.
            var dot = initial.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : initial.Length);
        };

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    // Ein Dialog, der sich nur einmal bauen lässt: Geht sein Fenster zu, endet er wie abgebrochen.
    private Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellation)
    {
        ContentDialog? once = dialog;
        return ShowAsync(
            () =>
            {
                var next = once;
                once = null;
                return next;
            },
            cancellation);
    }

    // `create` baut den Dialog für jedes Fenster, in dem er erscheint; liefert es nichts, endet er wie abgebrochen.
    private async Task<ContentDialogResult> ShowAsync(Func<ContentDialog?> create, CancellationToken cancellation)
    {
        await _oneAtATime.WaitAsync(cancellation);
        try
        {
            // Geht das Fenster des Dialogs zu (ein abgelöstes Terminal), zieht er in das Fenster, das dann vorn ist –
            // sonst verschwände er ohne Antwort (Interop.TerminalWindows.Closing).
            while (create() is { } dialog)
            {
                var moved = false;
                void OnWindowClosing(XamlRoot closing)
                {
                    if (ReferenceEquals(closing, dialog.XamlRoot))
                    {
                        moved = true;
                        dialog.Hide();
                    }
                }

                Interop.TerminalWindows.Closing += OnWindowClosing;
                try
                {
                    var result = await ShowOnceAsync(dialog, cancellation);
                    if (!moved)
                    {
                        return result;
                    }
                }
                finally
                {
                    Interop.TerminalWindows.Closing -= OnWindowClosing;
                }
            }

            return ContentDialogResult.None;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<ContentDialogResult> ShowOnceAsync(ContentDialog dialog, CancellationToken cancellation)
    {
        dialog.XamlRoot = xamlRoot() ?? throw new InvalidOperationException("No window to show the dialog in.");

        // Ein Dialog rollt seinen Inhalt nicht von selbst: Was in einem niedrigen Fenster nicht hineinpasst,
        // wäre abgeschnitten und nicht zu erreichen. Ein Inhalt, der nur untereinander stapelt, bekommt deshalb
        // einen Rollbereich; wer sein Layout selbst regelt (Grid, eigener ScrollViewer), bleibt, wie er ist.
        if (dialog.Content is StackPanel stacked)
        {
            dialog.Content = null;
            dialog.Content = new ScrollViewer
            {
                Content = stacked,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }

        // Abbruch (z. B. die Sitzung endet, während gefragt wird) schließt den Dialog. Die Meldung kommt
        // auf dem UI-Thread an, siehe SessionViewModel.
        using var registration = cancellation.Register(dialog.Hide);

        // Geht die Rückfrage auf, während ein anderes Programm vorn ist (eine Sitzung verbindet von selbst neu
        // und braucht das Passwort), macht die Taskleiste darauf aufmerksam.
        if (dialog.XamlRoot.ContentIslandEnvironment is { } island)
        {
            Interop.Attention.Ask(Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId));
        }

        var result = await dialog.ShowAsync();
        cancellation.ThrowIfCancellationRequested();
        return result;
    }
}
