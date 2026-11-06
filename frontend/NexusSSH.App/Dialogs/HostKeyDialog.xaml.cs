using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Dialogs;

public sealed partial class HostKeyDialog : ContentDialog
{
    public HostKeyDialog(ConnectionPrompt.HostKey prompt)
    {
        InitializeComponent();

        // Eine abgeleitete ContentDialog-Klasse findet den Standardstil nicht von selbst.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        // Bei einem Zertifikat (FTPS) gehört der Port immer dazu: 22 wäre dort nicht der übliche.
        ServerText.Text = prompt.Port == 22 && !HostKeyWording.IsCertificate(prompt) ? prompt.Host : $"{prompt.Host}:{prompt.Port}";
        AlgorithmText.Text = HostKeyWording.Kind(prompt);
        FingerprintText.Text = prompt.Fingerprint;
        Title = HostKeyWording.Title(prompt);
        Intro.Text = HostKeyWording.Intro(prompt);
        Hint.Text = HostKeyWording.Hint(prompt);
        PrimaryButtonText = HostKeyWording.Accept(prompt);

        if (prompt.Changed)
        {
            ChangedWarning.Title = L.Get("HostKeyChangedWarningTitle");
            ChangedWarning.Message = HostKeyWording.Warning(prompt);
            ChangedWarning.Visibility = Visibility.Visible;
            if (prompt.PreviousFingerprint is { Length: > 0 } previous)
            {
                PreviousText.Text = previous;
                PreviousLabel.Visibility = PreviousText.Visibility = Visibility.Visible;
            }
        }
    }
}
