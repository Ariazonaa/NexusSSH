using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class KeysView : UserControl
{
    public KeysView()
    {
        InitializeComponent();

        // Gelesen wird die Liste, wenn die Seite aufgeht (ShellViewModel.HomePage) – hier nur gezeigt.
        Shell.KeyFiles.CollectionChanged += (_, _) => ShowKeys();
        ShowKeys();
    }

    public ShellViewModel Shell => App.Shell;

    private void ShowKeys()
    {
        var any = Shell.KeyFiles.Count > 0;
        NoKeysText.Visibility = Ui.CollapsedIf(any);
        KeysList.Visibility = Ui.VisibleIf(any);
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Shell.LoadKeyFiles();

    // Nur der öffentliche Teil – die Zeile, die in authorized_keys eines Servers gehört.
    private void OnCopyPublicClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KeyFile { PublicKey.Length: > 0 } key)
        {
            return;
        }

        var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (Interop.TextClipboard.Set(window, key.PublicKey, html: null, rtf: null))
        {
            Shell.Log(L.Get("LogKeyPublicCopied", key.Name));
        }
    }

    private async void OnPassphraseClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KeyFile key)
        {
            return;
        }

        try
        {
            await App.Dialogs.ChangeKeyPassphraseAsync(key, (passphrase, next) => Shell.ChangeKeyPassphraseAsync(key, passphrase, next));
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error);
        }
    }

    private async void OnConvertClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KeyFile key)
        {
            return;
        }

        try
        {
            await App.Dialogs.ConvertKeyAsync(key, (passphrase, name) => Shell.ConvertKeyAsync(key, passphrase, name));
        }
        catch (Exception error)
        {
            App.WriteCrashLog(error);
        }
    }
}
