using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

// Die beiden Dialoge der Seite „Schlüssel“. Eine Passphrase steht nur in ihrem Feld und geht von dort an den Kern;
// scheitert es, bleibt der Dialog mit dem Satz offen, woran es lag.
public sealed partial class DialogService
{
    /// <summary>
    /// Ändert die Passphrase einer Schlüsseldatei: die bisherige (wenn es eine gibt), die neue zweimal – ein
    /// Tippfehler darin sperrte den Schlüssel für immer. <paramref name="apply"/> bekommt die bisherige und die neue
    /// und nennt, woran es lag (<c>null</c> = gelungen). <c>true</c>, wenn geändert wurde.
    /// </summary>
    public async Task<bool> ChangeKeyPassphraseAsync(KeyFile key, Func<string?, string?, Task<string?>> apply)
    {
        var hint = KeyCaption(key.Format == KeyFormat.Pem ? L.Get("KeyPassphrasePemHint") : L.Get("KeyPassphraseHint"), "TextFillColorSecondaryBrush");
        var current = new PasswordBox { Header = L.Get("KeyPassphraseCurrent"), Visibility = Ui.VisibleIf(key.IsEncrypted) };
        var next = new PasswordBox { Header = L.Get("KeyPassphraseNew") };
        var repeat = new PasswordBox { Header = L.Get("KeyPassphraseRepeatLabel") };
        var bare = KeyCaption(L.Get("KeyPassphraseNone"), "SystemFillColorCautionBrush");
        var problem = KeyCaption(string.Empty, "SystemFillColorCautionBrush");
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var dialog = new ContentDialog
        {
            Title = L.Get("KeyPassphraseTitle", key.Name),
            Content = KeyPanel(hint, current, next, repeat, bare, problem),
            PrimaryButtonText = L.Get("KeyPassphraseApply"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var busy = false;
        void Show()
        {
            // Ein verschlüsselter Schlüssel ohne neue Passphrase: Das ist erlaubt, soll aber niemandem unterlaufen.
            bare.Visibility = Ui.VisibleIf(key.IsEncrypted && next.Password.Length == 0 && repeat.Password.Length == 0);
            problem.Visibility = Ui.VisibleIf(problem.Text.Length > 0);
            dialog.IsPrimaryButtonEnabled = !busy
                && next.Password == repeat.Password
                && (key.IsEncrypted ? current.Password.Length > 0 : next.Password.Length > 0);
        }

        void Edited()
        {
            problem.Text = string.Empty;
            Show();
        }

        current.PasswordChanged += (_, _) => Edited();
        next.PasswordChanged += (_, _) => Edited();
        repeat.PasswordChanged += (_, _) => Edited();
        Show();

        var changed = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // Der Kern rechnet daran spürbar: Der Dialog wartet, statt sich zu schließen.
            var deferral = args.GetDeferral();
            busy = true;
            Show();
            try
            {
                if (await apply(key.IsEncrypted ? current.Password : null, next.Password) is { } failed)
                {
                    args.Cancel = true;
                    problem.Text = failed;
                }
                else
                {
                    changed = true;
                }
            }
            catch (Exception error)
            {
                App.WriteCrashLog(error);
                args.Cancel = true;
                problem.Text = L.Get("KeyFailed");
            }
            finally
            {
                busy = false;
                Show();
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => (key.IsEncrypted ? current : next).Focus(FocusState.Programmatic);

        await ShowAsync(dialog, CancellationToken.None);
        return changed;
    }

    /// <summary>
    /// Wandelt eine Schlüsseldatei in das andere Format um (<see cref="KeyFile.OtherFormat"/>): fragt nach dem Namen
    /// der neuen Datei und, bei einem verschlüsselten Schlüssel, nach seiner Passphrase. <paramref name="apply"/>
    /// bekommt Passphrase und Namen und nennt, woran es lag (<c>null</c> = gelungen). <c>true</c>, wenn die neue
    /// Datei entstand.
    /// </summary>
    public async Task<bool> ConvertKeyAsync(KeyFile key, Func<string?, string, Task<string?>> apply)
    {
        var hint = KeyCaption(
            L.Get("KeyConvertHint", Ui.KeyFormatName(key.OtherFormat), key.Name) + (key.IsEncrypted ? " " + L.Get("KeyConvertSamePassphrase") : string.Empty),
            "TextFillColorSecondaryBrush");
        var name = new TextBox { Header = L.Get("KeyConvertName"), Text = key.ConvertedName, IsSpellCheckEnabled = false, MaxLength = 64 };
        var passphrase = new PasswordBox { Header = L.Get("KeyConvertPassphrase"), Visibility = Ui.VisibleIf(key.IsEncrypted) };
        var problem = KeyCaption(string.Empty, "SystemFillColorCautionBrush");
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var dialog = new ContentDialog
        {
            Title = L.Get("KeyConvertTitle", key.Name),
            Content = KeyPanel(hint, name, passphrase, problem),
            PrimaryButtonText = L.Get("KeyConvertApply"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var busy = false;
        void Show()
        {
            problem.Visibility = Ui.VisibleIf(problem.Text.Length > 0);
            dialog.IsPrimaryButtonEnabled = !busy
                && KeyRequest.IsValidName(name.Text.Trim())
                && (!key.IsEncrypted || passphrase.Password.Length > 0);
        }

        void Edited()
        {
            problem.Text = string.Empty;
            Show();
        }

        name.TextChanged += (_, _) => Edited();
        passphrase.PasswordChanged += (_, _) => Edited();
        Show();

        var converted = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            busy = true;
            Show();
            try
            {
                if (await apply(key.IsEncrypted ? passphrase.Password : null, name.Text) is { } failed)
                {
                    args.Cancel = true;
                    problem.Text = failed;
                }
                else
                {
                    converted = true;
                }
            }
            catch (Exception error)
            {
                App.WriteCrashLog(error);
                args.Cancel = true;
                problem.Text = L.Get("KeyFailed");
            }
            finally
            {
                busy = false;
                Show();
                deferral.Complete();
            }
        };
        dialog.Opened += (_, _) => (key.IsEncrypted ? (Control)passphrase : name).Focus(FocusState.Programmatic);

        await ShowAsync(dialog, CancellationToken.None);
        return converted;
    }

    private static ScrollViewer KeyPanel(params UIElement[] elements)
    {
        var content = new StackPanel { Width = 440, Spacing = 10 };
        foreach (var element in elements)
        {
            content.Children.Add(element);
        }

        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock KeyCaption(string text, string brush) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources[brush],
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };
}
