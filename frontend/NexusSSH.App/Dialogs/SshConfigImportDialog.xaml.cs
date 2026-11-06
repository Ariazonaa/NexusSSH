using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

public sealed partial class SshConfigImportDialog : ContentDialog
{
    public SshConfigImportDialog(SshConfigImportViewModel import)
    {
        Import = import;
        InitializeComponent();

        // Eine abgeleitete ContentDialog-Klasse findet den Standardstil nicht von selbst.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
    }

    public SshConfigImportViewModel Import { get; }
}
