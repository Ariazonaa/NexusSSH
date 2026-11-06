using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Dialogs;

public sealed partial class DialogService
{
    /// <summary>
    /// Vor einem Ordner-Transfer: Liegt in den gewählten Ordnern etwas, das nach den Ausschlüssen nicht mitginge,
    /// zeigt die Rückfrage es je Muster und lässt wählen, was trotzdem mit soll. Liefert die Muster, die für diesen
    /// Transfer gelten – alle, wenn nichts darauf passt (dann wird gar nicht gefragt) –, und <c>null</c>, wenn der
    /// Benutzer abbricht. <paramref name="find"/> sucht, was die Muster träfen.
    /// </summary>
    public async Task<IReadOnlyList<string>?> AskExcludesAsync(TransferExcludes rules, Func<TransferExcludes, Task<IReadOnlyList<ExcludedItem>>> find)
    {
        if (rules.IsEmpty)
        {
            return [];
        }

        var groups = rules.Group(await find(rules));
        if (groups.Count == 0)
        {
            return rules.Patterns;
        }

        var content = new StackPanel { Width = 460, Spacing = 8 };
        content.Children.Add(new TextBlock { Text = L.Get("ExcludeAskText"), TextWrapping = TextWrapping.Wrap });
        List<(CheckBox Box, string Pattern)> boxes = [];
        foreach (var group in groups)
        {
            // Muster und Beispiel stammen aus den Einstellungen bzw. vom Server oder von der Platte: nur als Text.
            var title = NexusSSH.Core.Format.Visible(group.Pattern);
            var box = new CheckBox { Content = L.Get("ExcludeAskTake", title) };
            AutomationProperties.SetName(box, L.Get("ExcludeAskTake", title));
            var detail = new TextBlock
            {
                Text = L.Get("ExcludeAskFound", Counts(group), NexusSSH.Core.Format.Visible(group.Example.Length > 120 ? group.Example[..120] + "…" : group.Example)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, -6, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            boxes.Add((box, group.Pattern));
            content.Children.Add(box);
            content.Children.Add(detail);
        }

        content.Children.Add(new TextBlock
        {
            Text = L.Get("ExcludeAskHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            Title = L.Get("ExcludeAskTitle"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L.Get("ExcludeAskStart"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary)
        {
            return null;
        }

        // Was angehakt ist, geht mit: Sein Muster gilt für diesen Transfer nicht.
        var taken = boxes.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Pattern).ToHashSet(StringComparer.Ordinal);
        return [.. rules.Patterns.Where(pattern => !taken.Contains(pattern))];

        static string Counts(ExcludedGroup group) => (group.Folders, group.Files) switch
        {
            ( > 0, > 0) => L.Get("ExcludeAskBoth", group.Folders, group.Files),
            ( > 0, _) => L.Get("ExcludeAskFolders", group.Folders),
            _ => L.Get("ExcludeAskFiles", group.Files),
        };
    }
}
