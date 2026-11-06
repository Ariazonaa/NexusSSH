using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.App.Dialogs;

public sealed partial class DialogService
{
    /// <summary>
    /// Legt einen Tunnel an oder ändert ihn: zu welchem Server, welche Art, welche Ports und wohin. Liefert Server
    /// und Tunnel; <c>null</c> bei Abbruch. Bei einem vorhandenen Tunnel steht der Server fest.
    /// </summary>
    public async Task<(ServerProfile Server, ProfileTunnel Tunnel)?> EditTunnelAsync(IReadOnlyList<ServerProfile> servers, ServerProfile? server, ProfileTunnel? tunnel)
    {
        if (servers.Count == 0)
        {
            return null;
        }

        var which = new ComboBox { Header = L.Get("TunnelServer"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = tunnel is null };
        foreach (var candidate in servers)
        {
            which.Items.Add(new ComboBoxItem { Content = NexusSSH.Core.Format.Visible(candidate.Name), Tag = candidate });
        }

        which.SelectedIndex = Math.Max(0, servers.ToList().FindIndex(candidate => candidate.Id == server?.Id));

        var kind = new ComboBox { Header = L.Get("TunnelKind"), HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.Items.Add(new ComboBoxItem { Content = L.Get("TunnelKindLocalLong") });
        kind.Items.Add(new ComboBoxItem { Content = L.Get("TunnelKindRemoteLong") });
        kind.Items.Add(new ComboBoxItem { Content = L.Get("TunnelKindDynamicLong") });
        kind.SelectedIndex = (int)(tunnel?.Kind ?? TunnelKind.Local);

        var name = new TextBox { Header = L.Get("TunnelName"), Text = tunnel?.Name ?? string.Empty, IsSpellCheckEnabled = false, MaxLength = 64 };
        var listen = Port(tunnel?.ListenPort);
        var host = new TextBox { Text = tunnel?.TargetHost ?? string.Empty, IsSpellCheckEnabled = false, PlaceholderText = "127.0.0.1", MaxLength = 255 };
        var port = Port(tunnel is { TargetPort: > 0 } ? tunnel.TargetPort : null);
        port.Header = L.Get("TunnelTargetPort");
        (port.Width, port.HorizontalAlignment) = (double.NaN, HorizontalAlignment.Stretch);

        var target = new Grid { ColumnSpacing = 12 };
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        Grid.SetColumn(port, 1);
        target.Children.Add(host);
        target.Children.Add(port);

        var open = new CheckBox { Content = L.Get("TunnelOpen"), IsChecked = tunnel?.Open ?? false };
        var openHint = new TextBlock
        {
            Text = L.Get("TunnelOpenHint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -6, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        var auto = new CheckBox { Content = L.Get("TunnelAuto"), IsChecked = tunnel?.Auto ?? true };
        var problem = new TextBlock
        {
            Text = L.Get("TunnelInvalid"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var content = new StackPanel { Width = 480, Spacing = 12 };
        foreach (var element in (UIElement[])[which, kind, name, listen, target, open, openHint, auto, problem])
        {
            content.Children.Add(element);
        }

        var dialog = new ContentDialog
        {
            Title = tunnel is null ? L.Get("TunnelDialogNew") : L.Get("TunnelDialogEdit"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L.Get("Save"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        ProfileTunnel Draft() => new(
            tunnel?.Id ?? string.Empty,
            (TunnelKind)Math.Max(0, kind.SelectedIndex),
            name.Text.Trim(),
            Number(listen),
            kind.SelectedIndex == (int)TunnelKind.Dynamic ? string.Empty : host.Text.Trim(),
            kind.SelectedIndex == (int)TunnelKind.Dynamic ? 0 : Number(port),
            open.IsChecked == true,
            auto.IsChecked == true);

        // Was das Feld meint, hängt an der Art: Der Port lauscht hier oder am Server, das Ziel wird von der
        // jeweils anderen Seite aus gesehen – und ein dynamischer Tunnel hat keines.
        void Show()
        {
            var chosen = (TunnelKind)Math.Max(0, kind.SelectedIndex);
            listen.Header = chosen == TunnelKind.Remote ? L.Get("TunnelListenThere") : L.Get("TunnelListenHere");
            host.Header = chosen == TunnelKind.Remote ? L.Get("TunnelTargetFromHere") : L.Get("TunnelTargetFromServer");
            target.Visibility = Ui.VisibleIf(chosen != TunnelKind.Dynamic);
            openHint.Visibility = Ui.VisibleIf(open.IsChecked == true);
            var valid = Draft().IsValid;
            problem.Visibility = Ui.VisibleIf(!valid && (Number(listen) != 0 || host.Text.Length > 0));
            dialog.IsPrimaryButtonEnabled = valid;
        }

        kind.SelectionChanged += (_, _) => Show();
        host.TextChanged += (_, _) => Show();
        listen.ValueChanged += (_, _) => Show();
        port.ValueChanged += (_, _) => Show();
        open.Checked += (_, _) => Show();
        open.Unchecked += (_, _) => Show();
        Show();
        dialog.Opened += (_, _) => (tunnel is null ? (Control)kind : name).Focus(FocusState.Programmatic);

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary
            && (which.SelectedItem as ComboBoxItem)?.Tag is ServerProfile chosenServer
            && Draft() is { IsValid: true } draft
                ? (chosenServer, draft)
                : null;

        static NumberBox Port(int? value) => new()
        {
            Minimum = 1,
            Maximum = 65535,
            Value = value is > 0 ? value.Value : double.NaN,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 240,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsGrouped = false },
        };

        static int Number(NumberBox box) => double.IsFinite(box.Value) && box.Value is >= 1 and <= 65535 ? (int)box.Value : 0;
    }
}
