using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;

namespace NexusSSH.App.Dialogs;

public sealed partial class SecretDialog : ContentDialog
{
    private readonly List<Control> _inputs = [];

    public SecretDialog(ConnectionPrompt.Secret prompt)
    {
        InitializeComponent();

        // Eine abgeleitete ContentDialog-Klasse findet den Standardstil nicht von selbst.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        var account = $"{prompt.User}@{prompt.Host}";
        (Title, Intro.Text) = prompt.Kind switch
        {
            SecretKind.Passphrase => (L.Get("PassphraseTitle"), L.Get("PassphraseIntro", prompt.KeyPath ?? string.Empty)),
            SecretKind.KeyboardInteractive => (
                string.IsNullOrWhiteSpace(prompt.Name) ? L.Get("InteractiveTitle") : prompt.Name,
                L.Get("InteractiveIntro", account)),
            _ => (L.Get("PasswordTitle"), L.Get("PasswordIntro", account)),
        };

        if (!string.IsNullOrWhiteSpace(prompt.Instructions))
        {
            Instructions.Text = prompt.Instructions;
            Instructions.Visibility = Visibility.Visible;
        }

        // Ab dem zweiten Versuch war die vorige Eingabe falsch – nur bei Passwort und Passphrase, die Runden
        // der Serverfragen zählen anders.
        RetryWarning.Visibility = Ui.VisibleIf(prompt.Attempt > 1 && prompt.Kind != SecretKind.KeyboardInteractive);
        SaveBox.Visibility = Ui.VisibleIf(prompt.CanSave);

        foreach (var field in prompt.Fields)
        {
            var header = prompt.Kind switch
            {
                SecretKind.Password => L.Get("PasswordField"),
                SecretKind.Passphrase => L.Get("PassphraseField"),
                _ => field.Text,
            };

            // Sichtbare Eingabe nur, wenn der Server es ausdrücklich erlaubt (z. B. ein Benutzername).
            Control input = field.Echo
                ? new TextBox { Header = header }
                : new PasswordBox { Header = header };
            _inputs.Add(input);
            Fields.Children.Add(input);
        }

        Opened += (_, _) => _inputs.FirstOrDefault()?.Focus(FocusState.Programmatic);
    }

    /// <summary>Die Eingaben, eine je Feld, und ob sie gespeichert werden sollen.</summary>
    public SecretAnswer Answer => new(
        [.. _inputs.Select(input => input is PasswordBox password ? password.Password : ((TextBox)input).Text)],
        SaveBox.IsChecked == true);
}
