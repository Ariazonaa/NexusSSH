using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

// Dialoge des Dateibrowsers, die mehr als eine Frage sind: mehrere umbenennen.
public sealed partial class DialogService
{
    /// <summary>
    /// Eine Frage mit zwei Antworten und „Abbrechen“: <c>true</c> für die erste, <c>false</c> für die zweite,
    /// <c>null</c> bei Abbruch.
    /// </summary>
    public async Task<bool?> ChooseAsync(string title, string text, string first, string second)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = first,
            SecondaryButtonText = second,
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog, CancellationToken.None) switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    /// <summary>
    /// Fragt nach dem Passwort für <c>sudo</c>. Es geht von hier an den Kern und von dort an <c>sudo</c> – es wird
    /// weder gespeichert noch angezeigt. <c>null</c> bei Abbruch oder leerer Eingabe.
    /// </summary>
    public async Task<string?> AskSudoPasswordAsync(string server, string? confirm = null)
    {
        var box = new PasswordBox { Header = L.Get("RootPasswordLabel"), Width = 360 };
        var content = new StackPanel { Width = 440, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = L.Get("RootPasswordText", server), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = L.Get("RootPasswordTitle"),
            Content = content,
            PrimaryButtonText = confirm ?? L.Get("RootConfirm"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    /// <summary>
    /// Mehrere Einträge nach einer Regel umbenennen, mit Vorschau. Liefert, was umzubenennen ist, oder <c>null</c>
    /// bei Abbruch.
    /// </summary>
    public async Task<IReadOnlyList<(string From, string To)>?> BatchRenameAsync(BatchRenameViewModel rename)
    {
        TextBox Field(string header, string placeholder = "") => new()
        {
            Header = header,
            PlaceholderText = placeholder,
            IsSpellCheckEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var (find, replace, pattern) = (Field(L.Get("BatchRenameFind")), Field(L.Get("BatchRenameReplace")), Field(L.Get("BatchRenamePattern"), "{name}{ext}"));
        var ignoreCase = new CheckBox { Content = L.Get("BatchRenameIgnoreCase") };
        var start = new NumberBox { Header = L.Get("BatchRenameStart"), Value = 1, Minimum = 0, Maximum = 1_000_000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
        var digits = new NumberBox { Header = L.Get("BatchRenameDigits"), Value = 1, Minimum = 1, Maximum = 9, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
        var hint = new TextBlock { Text = L.Get("BatchRenameHint"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };

        // Die Vorschau ist reiner Text: Namen vom Server, ohne Steuerzeichen (RenamePreview), in fester Breite.
        var preview = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetName(preview, L.Get("BatchRenamePreview"));
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var words = new Grid { ColumnSpacing = 12 };
        words.ColumnDefinitions.Add(new ColumnDefinition());
        words.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(replace, 1);
        words.Children.Add(find);
        words.Children.Add(replace);

        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        numbers.Children.Add(start);
        numbers.Children.Add(digits);

        var content = new StackPanel { Width = 480, Spacing = 10 };
        foreach (var element in (UIElement[])[words, ignoreCase, pattern, numbers, hint, new ScrollViewer { Content = preview, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, summary])
        {
            content.Children.Add(element);
        }

        var dialog = new ContentDialog
        {
            Title = L.Get("BatchRenameTitle"),
            Content = content,
            PrimaryButtonText = L.Get("Rename"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        void Show()
        {
            preview.Text = string.Join("\n", rename.Lines.Select(line => line.ToString()));
            summary.Text = rename.Summary;
            dialog.IsPrimaryButtonEnabled = rename.CanApply;
        }

        find.TextChanged += (_, _) => { rename.Find = find.Text; Show(); };
        replace.TextChanged += (_, _) => { rename.Replace = replace.Text; Show(); };
        pattern.TextChanged += (_, _) => { rename.Pattern = pattern.Text; Show(); };
        ignoreCase.Click += (_, _) => { rename.IgnoreCase = ignoreCase.IsChecked == true; Show(); };
        start.ValueChanged += (_, _) => { rename.Start = start.Value; Show(); };
        digits.ValueChanged += (_, _) => { rename.Digits = digits.Value; Show(); };
        dialog.Opened += (_, _) => find.Focus(FocusState.Programmatic);
        Show();

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? rename.Changes : null;
    }

    /// <summary>
    /// Zeigt, was unter einem Verzeichnis des Servers wie viel Platz belegt – das Größte zuerst, mit Balken. Ein
    /// Ordner in der Liste ist ein Knopf: Er führt hinein. Die Zeilen entstehen im Code; Namen vom Server sind
    /// reiner Text ohne Steuerzeichen (<see cref="UsageRow.Name"/>).
    /// </summary>
    public async Task ShowUsageAsync(FolderUsageViewModel usage)
    {
        var path = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
        var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetName(up, L.Get("UsageUp"));
        ToolTipService.SetToolTip(up, L.Get("UsageUp"));
        var busy = new ProgressRing { Width = 16, Height = 16, IsActive = false };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(path, 1);
        Grid.SetColumn(busy, 2);
        head.Children.Add(up);
        head.Children.Add(path);
        head.Children.Add(busy);

        var rows = new StackPanel { Spacing = 2 };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(summary, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Width = 480, Spacing = 10 };
        content.Children.Add(head);
        content.Children.Add(new ScrollViewer { Content = rows, Height = 360 });
        content.Children.Add(summary);

        void Show()
        {
            path.Text = usage.PathShown;
            up.IsEnabled = usage.CanGoUp && !usage.IsBusy;
            busy.IsActive = usage.IsBusy;
            summary.Text = usage.Summary;
            rows.Children.Clear();
            foreach (var row in usage.Rows)
            {
                var line = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 2, 4, 2) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                var icon = new FontIcon { Glyph = row.IsDirectory ? "" : "", FontSize = 14 };
                var name = new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var bar = new ProgressBar { Value = row.Percent, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
                var size = new TextBlock { Text = row.SizeText, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                Grid.SetColumn(bar, 2);
                Grid.SetColumn(size, 3);
                line.Children.Add(icon);
                line.Children.Add(name);
                line.Children.Add(bar);
                line.Children.Add(size);

                if (row.IsDirectory)
                {
                    var open = new Button
                    {
                        Content = line,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        IsEnabled = !usage.IsBusy,
                    };
                    AutomationProperties.SetName(open, row.ToString());
                    open.Click += (_, _) => _ = usage.OpenAsync(row);
                    rows.Children.Add(open);
                }
                else
                {
                    AutomationProperties.SetName(line, row.ToString());
                    rows.Children.Add(line);
                }
            }
        }

        up.Click += (_, _) => _ = usage.GoUpAsync();
        usage.PropertyChanged += (_, _) => Show();
        var dialog = new ContentDialog
        {
            Title = L.Get("UsageTitle"),
            Content = content,
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Opened += (_, _) => _ = usage.LoadAsync();
        Show();
        await ShowAsync(dialog, CancellationToken.None);
    }
}
