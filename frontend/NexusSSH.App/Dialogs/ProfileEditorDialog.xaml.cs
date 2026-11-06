using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

public sealed partial class ProfileEditorDialog : ContentDialog
{
    public ProfileEditorDialog(ProfileEditorViewModel editor, IReadOnlyList<string> groups)
    {
        Editor = editor;
        InitializeComponent();

        // Eine abgeleitete ContentDialog-Klasse findet den Standardstil nicht von selbst.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        GroupBox.ItemsSource = groups;
        JumpBox.SelectedItem = editor.Jump;
        ColorBox.SelectedItem = editor.Color;
        ThemeBox.SelectedItem = editor.Theme;
        IdentityBox.SelectedItem = editor.Identity;
        LinkBox.SelectedItem = editor.Link;
        AddressFamilyBox.SelectedItem = editor.AddressFamily;
        LocalShellBox.SelectedIndex = editor.LocalShellIndex;
        ShowSectionsFor(editor.IsLocal);
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProfileEditorViewModel.IsLocal))
            {
                ShowSectionsFor(editor.IsLocal);
            }
        };
        Opened += (_, _) => HostBox.Focus(FocusState.Programmatic);
    }

    public ProfileEditorViewModel Editor { get; }

    // Ein lokales Terminal hat keine Anmeldung, keinen Weg und keine Verbindung: Deren Reiter fallen weg. War einer
    // davon offen, geht es zurück zum ersten.
    private void ShowSectionsFor(bool isLocal)
    {
        var visibility = Ui.VisibleIf(!isLocal);
        AuthSection.Visibility = visibility;
        RouteSection.Visibility = visibility;
        LinkSection.Visibility = visibility;
        if (isLocal && Sections.SelectedItem is { } selected && selected != ServerSection && selected != NotesSection)
        {
            Sections.SelectedItem = ServerSection;
        }
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Die Vorauswahl im XAML meldet sich, bevor alle Elemente da sind.
        if (ServerPanel is null || AuthPanel is null || RoutePanel is null || LinkPanel is null || NotesPanel is null)
        {
            return;
        }

        var selected = Sections.SelectedItem;
        ServerPanel.Visibility = Ui.VisibleIf(selected == ServerSection);
        AuthPanel.Visibility = Ui.VisibleIf(selected == AuthSection);
        RoutePanel.Visibility = Ui.VisibleIf(selected == RouteSection);
        LinkPanel.Visibility = Ui.VisibleIf(selected == LinkSection);
        NotesPanel.Visibility = Ui.VisibleIf(selected == NotesSection);
    }

    // SelectedItem ist object; die Nummer in der Liste des ViewModels dagegen eindeutig.
    private void OnLocalShellChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalShellBox.SelectedIndex >= 0)
        {
            Editor.LocalShellIndex = LocalShellBox.SelectedIndex;
        }
    }

    private void OnLinkChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinkBox.SelectedItem is LinkChoice choice)
        {
            Editor.Link = choice;
        }
    }

    // SelectedItem ist object, die Auswahl im ViewModel typisiert – deshalb von Hand.
    private void OnJumpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JumpBox.SelectedItem is JumpChoice choice)
        {
            Editor.Jump = choice;
        }
    }

    private void OnIdentityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IdentityBox.SelectedItem is IdentityChoice choice)
        {
            Editor.Identity = choice;
        }
    }

    // Testet, was gerade im Dialog steht. Der Dialog bleibt bedienbar; ein zweiter Klick währenddessen tut nichts.
    private async void OnProbeClicked(object sender, RoutedEventArgs e)
    {
        if (ProbeRing.IsActive || !Editor.IsValid)
        {
            return;
        }

        ProbeRing.IsActive = true;
        ProbeResult.Text = L.Get("ProbeRunning");
        try
        {
            ProbeResult.Text = await App.Shell.ProbeAsync(Editor.ToProfile());
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
            ProbeResult.Text = L.Get("ProbeFailed");
        }
        finally
        {
            ProbeRing.IsActive = false;
        }
    }

    private void OnAddressFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressFamilyBox.SelectedItem is AddressFamilyChoice choice)
        {
            Editor.AddressFamily = choice;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ThemeChoice theme)
        {
            Editor.Theme = theme;
        }
    }

    private void OnColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorBox.SelectedItem is AccentColor color)
        {
            Editor.Color = color;
        }
    }

    private async void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Unspecified,
        };
        picker.FileTypeFilter.Add("*");

        // Abbruch liefert null; dann bleibt das Feld, wie es war.
        if (await picker.PickSingleFileAsync() is { } file)
        {
            Editor.KeyPath = file.Path;
        }
    }

    private async void OnBrowseLocalFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);

        // Abbruch liefert null; dann bleibt das Feld, wie es war.
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            Editor.LocalDirectory = folder.Path;
        }
    }
}
