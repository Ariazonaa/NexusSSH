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
                    new TextBlock { Text = L.Get(question.InTerminal ? "McpCommandHintTerminal" : "McpCommandHint"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = string.Join('\n', question.Command.Split('\n').Select(Format.Visible)),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    },
                    new TextBlock
                    {
                        Text = L.Get("McpCommandAlwaysHint"),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    left,
                },
            },
            PrimaryButtonText = L.Get("McpCommandRun"),
            SecondaryButtonText = L.Get("McpCommandAlways"),
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
