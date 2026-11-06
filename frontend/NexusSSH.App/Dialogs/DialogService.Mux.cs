using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

// Laufende Sitzungen von tmux und screen: woran anhängen, und was künftig nach dem Verbinden geschehen soll.
public sealed partial class DialogService
{
    /// <summary>
    /// Zeigt, was auf dem Server läuft. Liefert die gewählte Sitzung (<c>null</c> = nicht anhängen) und den
    /// Schritt, der für diesen Server gemerkt wird – oder <c>null</c>, wenn der Benutzer den Dialog abbricht (dann
    /// bleibt alles, wie es war). Die Namen kommen vom Server und stehen als reiner Text da.
    /// </summary>
    public async Task<(MuxSession? Chosen, MuxStep Step, bool Control)?> ChooseMuxAsync(SessionViewModel session, IReadOnlyList<MuxSession> running, MuxStep current, bool controlBefore)
    {
        var list = new ListView
        {
            ItemsSource = BindableList.Of(running),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MaxHeight = 260,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, L.Get("MuxListName"));

        var step = new ComboBox
        {
            Header = L.Get("MuxStepLabel"),
            ItemsSource = new List<string> { L.Get("MuxStepNone"), L.Get("MuxStepOffer"), L.Get("MuxStepAttach") },
            SelectedIndex = (int)current,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // tmux kann seine Fenster der App überlassen (Steuermodus); bei screen gibt es das nicht.
        var control = new CheckBox { Content = L.Get("MuxControl"), IsChecked = controlBefore };
        void Offer() => control.IsEnabled = list.SelectedItem is MuxSession { Kind: "tmux" };
        list.SelectionChanged += (_, _) => Offer();
        Offer();
        var hint = new TextBlock
        {
            Text = L.Get("MuxHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var dialog = new ContentDialog
        {
            Title = L.Get("MuxTitle", Format.Visible(session.Profile.Name)),
            Content = new StackPanel { Width = 440, Spacing = 12, Children = { list, control, step, hint } },
            PrimaryButtonText = L.Get("MuxAttach"),
            SecondaryButtonText = L.Get("MuxNotNow"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await ShowAsync(dialog, CancellationToken.None);
        if (result == ContentDialogResult.None)
        {
            return null;
        }

        var chosen = result == ContentDialogResult.Primary ? list.SelectedItem as MuxSession : null;
        var wanted = (MuxStep)Math.Clamp(step.SelectedIndex, 0, 2);

        // „Von selbst anhängen“ braucht eine Sitzung, an die angehängt wird – ohne Wahl bleibt es beim Anbieten.
        return (chosen, wanted == MuxStep.Attach && chosen is null ? MuxStep.Offer : wanted, control.IsChecked == true && chosen is { Kind: "tmux" });
    }
}
