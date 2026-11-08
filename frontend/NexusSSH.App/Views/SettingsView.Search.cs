using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Terminals;

namespace NexusSSH.App.Views;

// Suche und Vorgaben: Das Suchfeld oben zeigt aus allen Bereichen nur die Zeilen, die zum Getippten passen. Und
// jede Zeile, deren Einstellung nicht mehr auf der Vorgabe steht, bekommt rechts einen Knopf „Zurück zur Vorgabe“
// – der Knopf ist zugleich die Marke. Zu welcher Einstellung eine Zeile gehört, steht im XAML an ihr
// (Tag="set:<Schlüssel>"); die Schlüssel kennt ShellViewModel.ResetSetting.
public sealed partial class SettingsView
{
    private const string SettingTag = "set:";

    private readonly List<(string Key, Button Reset)> _marks = [];

    private (string Name, StackPanel Page, ListViewItem Nav)[] Pages =>
    [
        ("General", PageGeneral, NavGeneral), ("Look", PageLook, NavLook), ("Connections", PageConnections, NavConnections),
        ("Alerts", PageAlerts, NavAlerts), ("Terminal", PageTerminal, NavTerminal), ("Files", PageFiles, NavFiles),
        ("Shortcuts", PageShortcuts, NavShortcuts), ("Data", PageData, NavData), ("Mcp", PageMcp, NavMcp),
        ("About", PageAbout, NavAbout),
    ];

    private bool IsSearching => SearchBox.Text.Trim().Length > 0;

    // Die Felder, die diese Seite selbst füllt (nicht über x:Bind) – beim Start und nach „Zurück zur Vorgabe“.
    private void ShowChosen()
    {
        LanguageBox.SelectedIndex = (int)Settings.Language;
        TransferRuleBox.SelectedIndex = (int)Settings.TransferRule;
        var accents = NexusSSH.Core.Settings.AccentColors.All;
        AccentBox.SelectedItem = accents.FirstOrDefault(accent => accent.Id == Settings.Accent) ?? accents[0];
        CursorBox.SelectedIndex = Math.Max(0, TerminalLook.CursorStyles.ToList().IndexOf(Shell.TerminalCursor));
        MoshPredictBox.SelectedIndex = Math.Max(0, NexusSSH.Core.Settings.AppSettings.MoshPredictions.ToList().IndexOf(Shell.MoshPredict));
        McpPromptBox.SelectedIndex = Math.Max(0, NexusSSH.Core.Settings.AppSettings.McpPromptChoices.ToList().IndexOf(Shell.McpPromptSeconds));
        EditorBox.Text = Settings.EditorPath;
        CompareBox.Text = Settings.ComparePath;
    }

    // Jede Zeile mit einem Schlüssel wandert in ein Raster mit einer zweiten Spalte für den Knopf. Einmal, beim Aufbau.
    private void AddResetButtons()
    {
        // Wem eine Zeile gehört, sagt beim Aufbau der Seite noch nicht sie selbst (Parent ist da noch leer).
        var tagged = Pages.SelectMany(page => Placed(page.Page))
            .Where(placed => placed.Child is FrameworkElement { Tag: string tag } && tag.StartsWith(SettingTag, StringComparison.Ordinal))
            .Select(placed => (placed.Parent, Row: (FrameworkElement)placed.Child))
            .ToList();
        foreach (var (parent, row) in tagged)
        {
            var key = ((string)row.Tag)[SettingTag.Length..];
            var name = $"{L.Get("SettingReset")}: {Label(row)}";
            var reset = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                },
                Style = (Style)Application.Current.Resources["IconButtonStyle"],
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(reset, name);
            ToolTipService.SetToolTip(reset, name);
            reset.Click += (_, _) => Shell.ResetSetting(key);

            var index = parent.Children.IndexOf(row);
            parent.Children.RemoveAt(index);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(row);
            parent.Children.Insert(index, grid);
            Grid.SetColumn(reset, 1);
            grid.Children.Add(reset);
            _marks.Add((key, reset));
        }
    }

    private static IEnumerable<(Panel Parent, UIElement Child)> Placed(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            yield return (panel, child);
            if (child is Panel inner)
            {
                foreach (var below in Placed(inner))
                {
                    yield return below;
                }
            }
        }
    }

    private void ShowMarks()
    {
        foreach (var (key, reset) in _marks)
        {
            reset.Visibility = Ui.VisibleIf(!Shell.IsDefaultSetting(key));
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var found = false;
        var chosen = (NavList.SelectedItem as ListViewItem)?.Tag as string;
        foreach (var (name, page, nav) in Pages)
        {
            if (query.Length == 0)
            {
                ShowAll(page);
                page.Visibility = Ui.VisibleIf(name == chosen);
                continue;
            }

            // Passt schon der Name des Bereichs, gehört alles darin zum Fund.
            var any = Filter(page, query, SettingsSearch.Matches(nav.Content as string, query));
            page.Visibility = Ui.VisibleIf(any);
            found |= any;
        }

        SearchEmpty.Visibility = Ui.VisibleIf(query.Length > 0 && !found);
        PageScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    // Ein Bereich besteht aus Karten (StackPanel), manche mit einer Überschrift davor; eine Karte aus Zeilen.
    private static bool Filter(StackPanel page, string query, bool whole)
    {
        var any = false;
        TextBlock? title = null;
        foreach (var child in page.Children)
        {
            if (child is TextBlock heading)
            {
                title = heading;
                heading.Visibility = Visibility.Collapsed;
                continue;
            }

            if (child is not StackPanel card)
            {
                continue;
            }

            var all = whole || SettingsSearch.Matches(title?.Text, query);
            var shown = false;
            foreach (var row in card.Children)
            {
                var match = all || SettingsSearch.Matches(TextOf(row), query);
                row.Visibility = Ui.VisibleIf(match);
                shown |= match;
            }

            card.Visibility = Ui.VisibleIf(shown);
            if (title is not null)
            {
                title.Visibility = Ui.VisibleIf(shown);
            }

            any |= shown;
            title = null;
        }

        return any;
    }

    private static void ShowAll(StackPanel page)
    {
        foreach (var child in page.Children)
        {
            child.Visibility = Visibility.Visible;
            if (child is StackPanel card)
            {
                foreach (var row in card.Children)
                {
                    row.Visibility = Visibility.Visible;
                }
            }
        }
    }

    // Der erste Text einer Zeile: ihr Name für den Knopf „Zurück zur Vorgabe“.
    private static string Label(UIElement row)
        => Texts(row).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim() ?? string.Empty;

    private static string TextOf(UIElement row) => string.Join(' ', Texts(row));

    // Alles Lesbare an einem Element und darunter: Texte, Beschriftungen, Überschriften der Felder.
    private static IEnumerable<string> Texts(UIElement element)
    {
        switch (element)
        {
            case TextBlock text:
                yield return text.Text;
                break;
            case ComboBox { Header: string header }:
                yield return header;
                break;
            case NumberBox { Header: string header }:
                yield return header;
                break;
            case TextBox { Header: string header }:
                yield return header;
                break;
            case Slider { Header: string header }:
                yield return header;
                break;
            case InfoBar bar:
                yield return bar.Message;
                break;
            case ContentControl { Content: string content }:
                yield return content;
                break;
            case Panel panel:
                foreach (var text in panel.Children.SelectMany(Texts))
                {
                    yield return text;
                }

                break;
        }
    }
}
