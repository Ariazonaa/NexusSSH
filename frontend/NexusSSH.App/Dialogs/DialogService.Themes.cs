using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Terminals;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

// Der Editor für ein eigenes Farbschema: Name, zwanzig Farbfelder, ein Farbwähler für das gewählte Feld und eine
// Vorschau. Die Regeln (Name, Form der Farben) stehen in Core (ThemeEditorViewModel).
public sealed partial class DialogService
{
    /// <summary>Zeigt den Editor; liefert das Schema, wenn der Benutzer speichert, sonst <c>null</c>.</summary>
    public async Task<TerminalTheme?> EditThemeAsync(ThemeEditorViewModel editor)
    {
        var name = new TextBox { Header = L.Get("ThemeName"), Text = editor.Name, MaxLength = ThemeImport.MaxName, IsSpellCheckEnabled = false };
        var nameError = new TextBlock
        {
            Text = editor.NameError,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Ui.VisibleIf(editor.NameError.Length > 0),
        };

        // Vorschau: ein paar Zeilen, wie sie im Terminal stünden.
        var previewText = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, TextWrapping = TextWrapping.NoWrap };
        var preview = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Child = previewText };
        AutomationProperties.SetName(preview, L.Get("ThemePreview"));

        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsColorPreviewVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,

            // Quer: Der Regler für die Helligkeit und das Hex-Feld stehen neben dem Farbfeld statt darunter. Hochkant
            // lagen sie unter dem Rand des Dialogs – und ohne den Regler bleibt Schwarz im Farbfeld schwarz.
            Orientation = Orientation.Horizontal,
            Color = Ui.ParseColor(editor.Selected.Hex),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectedLabel = new TextBlock { Text = editor.Selected.Label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

        // Die Felder: zwei Reihen – oben Hintergrund, Schrift, Cursor, Markierung und die acht dunklen Farben nicht,
        // sondern der Übersicht halber: vier besondere, dann zweimal acht.
        var accent = Ui.ChartBrush;
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var syncing = false;
        var swatches = new Dictionary<ThemeColorSlot, (Border Frame, Border Fill)>();
        StackPanel Row(IEnumerable<ThemeColorSlot> slots)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var slot in slots)
            {
                var fill = new Border { Width = 34, Height = 22, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Ui.ParseColor(slot.Hex)) };
                var button = new Button { Padding = new Thickness(3), Content = fill };
                ToolTipService.SetToolTip(button, slot.Label);
                AutomationProperties.SetName(button, slot.Summary);
                var frame = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), BorderBrush = slot.IsSelected ? accent : none, Child = button };
                swatches[slot] = (frame, fill);
                button.Click += (_, _) =>
                {
                    swatches[editor.Selected].Frame.BorderBrush = none;
                    editor.Select(slot);
                    frame.BorderBrush = accent;
                    selectedLabel.Text = slot.Label;

                    // Der Farbwähler zeigt jetzt dieses Feld – das ist keine Änderung der Farbe.
                    syncing = true;
                    picker.Color = Ui.ParseColor(slot.Hex);
                    syncing = false;
                };
                row.Children.Add(frame);
            }

            return row;
        }

        void Paint()
        {
            var theme = editor.ToTheme();
            preview.Background = new SolidColorBrush(Ui.ParseColor(theme.Background));
            previewText.Inlines.Clear();
            void Add(string text, string hex) => previewText.Inlines.Add(new Run { Text = text, Foreground = new SolidColorBrush(Ui.ParseColor(hex)) });
            Add("user@server", theme.Ansi[10]);
            Add(":", theme.Foreground);
            Add("~/www", theme.Ansi[12]);
            Add("$ ls -l\n", theme.Foreground);
            Add("drwxr-xr-x ", theme.Foreground);
            Add("assets  ", theme.Ansi[4]);
            Add("deploy.sh  ", theme.Ansi[2]);
            Add("backup.tar.gz\n", theme.Ansi[1]);
            Add("warning: ", theme.Ansi[3]);
            Add("disk 91% ", theme.Foreground);
            Add("error ", theme.Ansi[9]);
            Add("ok ", theme.Ansi[6]);
            Add("link", theme.Ansi[5]);
        }

        Paint();
        var content = new StackPanel { Width = 480, Spacing = 10 };
        foreach (var element in (UIElement[])
        [
            name,
            nameError,
            preview,
            Row(editor.Slots.Take(4)),
            Row(editor.Slots.Skip(4).Take(8)),
            Row(editor.Slots.Skip(12)),
            selectedLabel,
            picker,
        ])
        {
            content.Children.Add(element);
        }

        var dialog = new ContentDialog
        {
            Title = editor.Title,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = L.Get("Save"),
            CloseButtonText = L.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = editor.IsValid,
        };

        name.TextChanged += (_, _) =>
        {
            editor.Name = name.Text;
            nameError.Text = editor.NameError;
            nameError.Visibility = Ui.VisibleIf(editor.NameError.Length > 0);
            dialog.IsPrimaryButtonEnabled = editor.IsValid;
        };
        picker.ColorChanged += (_, args) =>
        {
            if (syncing)
            {
                return;
            }

            var color = args.NewColor;
            if (editor.SetColor($"#{color.R:x2}{color.G:x2}{color.B:x2}"))
            {
                swatches[editor.Selected].Fill.Background = new SolidColorBrush(color);
                Paint();
            }
        };

        return await ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary && editor.IsValid ? editor.ToTheme() : null;
    }
}
