using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.App.Dialogs;

// Rückfragen zur Mehrfachauswahl auf der Startseite: Gruppe, Schlagworte, Löschen.
public sealed partial class DialogService
{
    /// <summary>In welche Gruppe die markierten Server sollen. <c>null</c> bei Abbruch, leer = ohne Gruppe.</summary>
    public async Task<string?> AskGroupAsync(int count, IReadOnlyList<string> groups)
    {
        var box = new ComboBox { Header = L.Get("BulkGroupLabel"), IsEditable = true, ItemsSource = groups, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog
        {
            Title = L.Get("BulkGroupTitle", count),
            Content = new StackPanel { Width = 380, Children = { box } },
            PrimaryButtonText = L.Get("BulkMove"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        // Eine getippte Gruppe steht im Text, eine gewählte in der Auswahl.
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            ? (box.Text is { Length: > 0 } typed ? typed : box.SelectedItem as string ?? string.Empty).Trim()
            : null;
    }

    /// <summary>Welche Schlagworte dazukommen und welche wegfallen. <c>null</c> bei Abbruch.</summary>
    public async Task<(string Add, string Remove)?> AskTagsAsync(int count)
    {
        var add = new TextBox { Header = L.Get("BulkTagsAdd"), PlaceholderText = "web, nginx", IsSpellCheckEnabled = false };
        var remove = new TextBox { Header = L.Get("BulkTagsRemove"), IsSpellCheckEnabled = false };
        var dialog = new ContentDialog
        {
            Title = L.Get("BulkTagsTitle", count),
            Content = new StackPanel { Width = 380, Spacing = 10, Children = { add, remove } },
            PrimaryButtonText = L.Get("BulkApply"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? (add.Text, remove.Text) : null;
    }

    /// <summary>Rückfrage vor dem Löschen mehrerer Server: nennt die ersten beim Namen. „Abbrechen“ ist die Vorgabe.</summary>
    public Task<bool> ConfirmDeleteAsync(IReadOnlyList<ServerProfile> profiles)
    {
        const int Named = 8;
        var names = string.Join(", ", profiles.Take(Named).Select(profile => Format.Visible(profile.Name)));
        if (profiles.Count > Named)
        {
            names += " " + L.Get("BulkMore", profiles.Count - Named);
        }

        return ConfirmAsync(L.Get("BulkDeleteTitle", profiles.Count), $"{names}\n\n{L.Get("BulkDeleteText")}", L.Get("Delete"));
    }
}
