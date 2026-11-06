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
    /// Trägt eine Zertifizierungsstelle für Host-Zertifikate ein: für welche Server (Muster) und mit welchem
    /// öffentlichen Schlüssel. <paramref name="trust"/> versucht es und nennt, woran es lag (<c>null</c> = gelungen);
    /// der Dialog bleibt dann mit dem Satz offen. <c>true</c>, wenn eine Stelle eingetragen wurde.
    /// </summary>
    public async Task<bool> AddAuthorityAsync(Func<string, string, string?> trust)
    {
        var hint = Caption(L.Get("AuthorityDialogHint"), "TextFillColorSecondaryBrush");
        var patterns = new TextBox
        {
            Header = L.Get("AuthorityPatterns"),
            PlaceholderText = "*.example.org",
            IsSpellCheckEnabled = false,
            MaxLength = HostAuthority.MaxPatterns,
        };
        var patternsHint = Caption(L.Get("AuthorityPatternsHint"), "TextFillColorSecondaryBrush");
        var key = new TextBox
        {
            Header = L.Get("AuthorityKey"),
            PlaceholderText = "ssh-ed25519 AAAA…",
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
        };
        var keyHint = Caption(L.Get("AuthorityKeyHint"), "TextFillColorSecondaryBrush");
        var everything = Caption(L.Get("AuthorityEverything"), "SystemFillColorCautionBrush");
        var problem = Caption(string.Empty, "SystemFillColorCautionBrush");
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var content = new StackPanel { Width = 480, Spacing = 10 };
        foreach (var element in (UIElement[])[hint, patterns, patternsHint, everything, key, keyHint, problem])
        {
            content.Children.Add(element);
        }

        var dialog = new ContentDialog
        {
            Title = L.Get("AuthorityDialogTitle"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L.Get("AuthorityTrust"),
            CloseButtonText = L.Get("Cancel"),
            // Vertrauen für viele Server auf einmal: Die Eingabetaste soll es nicht versehentlich aussprechen.
            DefaultButton = ContentDialogButton.Close,
        };

        void Show()
        {
            everything.Visibility = Ui.VisibleIf(HostAuthority.CoversEverything(patterns.Text));
            problem.Visibility = Ui.VisibleIf(problem.Text.Length > 0);
            dialog.IsPrimaryButtonEnabled = HostAuthority.IsPatterns(patterns.Text) && HostAuthority.LooksLikeKey(key.Text.Trim());
        }

        void Edited()
        {
            problem.Text = string.Empty;
            Show();
        }

        patterns.TextChanged += (_, _) => Edited();
        key.TextChanged += (_, _) => Edited();
        Show();

        var added = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (trust(patterns.Text, key.Text) is { } failed)
            {
                // Der Dialog bleibt offen: Der Benutzer sieht, woran es lag, und kann es richten.
                args.Cancel = true;
                problem.Text = failed;
                Show();
            }
            else
            {
                added = true;
            }
        };
        dialog.Opened += (_, _) => patterns.Focus(FocusState.Programmatic);

        await ShowAsync(dialog, CancellationToken.None);
        return added;

        static TextBlock Caption(string text, string brush) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources[brush],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
    }
}
