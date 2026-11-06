using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class TunnelsView : UserControl
{
    public TunnelsView()
    {
        InitializeComponent();
        Shell.Tunnels.CollectionChanged += (_, _) => ShowEmpty();
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.TunnelServers))
            {
                ShowEmpty();
            }
        };
        ShowEmpty();
    }

    public ShellViewModel Shell => App.Shell;

    // Ohne Tunnel steht da, wozu sie gut sind – und ohne Server, dass es erst einen braucht.
    private void ShowEmpty()
    {
        var servers = Shell.TunnelServers.Count > 0;
        EmptyText.Text = servers ? L.Get("TunnelsEmpty") : L.Get("TunnelsNoServers");
        EmptyText.Visibility = Ui.VisibleIf(Shell.Tunnels.Count == 0);
        TunnelList.Visibility = Ui.VisibleIf(Shell.Tunnels.Count > 0);
        AddTunnelButton.IsEnabled = servers;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => _ = TunnelActions.EditAsync(Shell, Shell.ActiveSession?.Profile, null);

    // Der Schalter zeigt, was ist – und was er tun soll, geht als Bitte an die Verbindung. Kommt sie nicht zustande,
    // springt er mit der Meldung des Kerns zurück.
    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            TunnelActions.Toggled(Shell, toggle);
        }
    }

    private void OnMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TunnelRow row } anchor)
        {
            TunnelActions.Menu(Shell, row).ShowAt(anchor);
        }
    }
}

/// <summary>Was sich mit einem Tunnel tun lässt – dasselbe auf der Startseite und im Inspector.</summary>
internal static class TunnelActions
{
    /// <summary>
    /// Ein Schalter hat sich bewegt – vom Benutzer oder weil die Liste seine Zeile gerade neu belegt. Im zweiten
    /// Fall stehen Zeile (<c>Tag</c>) und Stellung für einen Augenblick nicht beisammen: Die Liste benutzt ihre
    /// Zeilen wieder, und so schaltete eine frisch aufgebaute Liste einen fremden Tunnel ein (gesehen 2026-10-13).
    /// Deshalb erst nachsehen, wenn alles gebunden ist: Stimmen Schalter und Tunnel dann überein, war es die Liste.
    /// </summary>
    public static void Toggled(ShellViewModel shell, ToggleSwitch toggle)
        => toggle.DispatcherQueue.TryEnqueue(() =>
        {
            if (toggle.Tag is TunnelRow row && toggle.IsOn != row.IsListening && !shell.SwitchTunnel(row, toggle.IsOn))
            {
                toggle.IsOn = row.IsListening;
            }
        });

    public static MenuFlyout Menu(ShellViewModel shell, TunnelRow row)
    {
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = L.Get("TunnelEdit"), Icon = new FontIcon { Glyph = "" } };
        edit.Click += (_, _) => _ = EditAsync(shell, row.Profile, row);
        var delete = new MenuFlyoutItem { Text = L.Get("TunnelDelete"), Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) => _ = DeleteAsync(shell, row);
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        return menu;
    }

    public static async Task EditAsync(ShellViewModel shell, NexusSSH.Core.Models.ServerProfile? server, TunnelRow? row)
    {
        try
        {
            if (await App.Dialogs.EditTunnelAsync(shell.TunnelServers, row?.Profile ?? server, row?.Tunnel) is not { } answer)
            {
                return;
            }

            if (!shell.SaveTunnel(answer.Server, answer.Tunnel))
            {
                await App.Dialogs.ShowMessageAsync(row is null ? L.Get("TunnelDialogNew") : L.Get("TunnelDialogEdit"), L.Get("TunnelSaveFailed"));
            }
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    private static async Task DeleteAsync(ShellViewModel shell, TunnelRow row)
    {
        try
        {
            if (await App.Dialogs.ConfirmAsync(L.Get("TunnelDeleteTitle"), L.Get("TunnelDeleteText", row.Name, row.Server), L.Get("TunnelDelete")))
            {
                shell.DeleteTunnel(row);
            }
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }
}
