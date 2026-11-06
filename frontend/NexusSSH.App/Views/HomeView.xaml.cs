using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NexusSSH.App.Controls;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Views;

public sealed partial class HomeView : UserControl
{
    // Während die Ansicht Auswahl und Umschalter selbst setzt, sind deren Ereignisse kein Klick.
    private bool _filling;

    public HomeView()
    {
        InitializeComponent();
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.HomePage) or nameof(ShellViewModel.IsHomeMoreOpen))
            {
                ShowPage();
            }
        };
        Ui.ContrastChanged += ShowPage;
        ShowPage();
    }

    public ShellViewModel Shell => App.Shell;

    // Die Einträge der Seitenleiste stehen in der Reihenfolge von HomeSidebar.Entries.
    private void ShowPage()
    {
        var page = Shell.HomePage;
        var open = Shell.IsHomeMoreOpen;
        var highlighted = HomeSidebar.Highlighted(page, open);

        _filling = true;
        try
        {
            SyncsEntry.Visibility = KnownHostsEntry.Visibility = OverviewEntry.Visibility = Ui.VisibleIf(open);
            if (Pages.SelectedIndex != (int)highlighted)
            {
                Pages.SelectedIndex = (int)highlighted;
            }

            KeysToggle.IsChecked = page == HomePage.Keys;
            IdentitiesToggle.IsChecked = page == HomePage.Identities;
        }
        finally
        {
            _filling = false;
        }

        // Der gewählte Eintrag trägt sein Symbol in der Akzentfarbe (im Kontrastdesign die für Hervorgehobenes); die
        // übrigen erben die Farbe ihrer Zeile.
        foreach (var (entry, icon) in new (HomeEntry, LineIcon)[]
        {
            (HomeEntry.Servers, ServersIcon), (HomeEntry.Snippets, SnippetsIcon), (HomeEntry.KeysAndIdentities, KeysIcon),
            (HomeEntry.Tunnels, TunnelsIcon), (HomeEntry.More, MoreIcon),
        })
        {
            if (entry == highlighted)
            {
                icon.Foreground = Ui.ChartBrush;
            }
            else
            {
                icon.ClearValue(ForegroundProperty);
            }
        }

        MoreRotation.Angle = open ? 0 : -90;
        KeysSwitch.Visibility = Ui.VisibleIf(page is HomePage.Keys or HomePage.Identities);

        ServersPage.Visibility = Ui.VisibleIf(page == HomePage.Servers);
        SyncsPage.Visibility = Ui.VisibleIf(page == HomePage.Syncs);
        SnippetsPage.Visibility = Ui.VisibleIf(page == HomePage.Snippets);
        IdentitiesPage.Visibility = Ui.VisibleIf(page == HomePage.Identities);
        KeysPage.Visibility = Ui.VisibleIf(page == HomePage.Keys);
        KnownHostsPage.Visibility = Ui.VisibleIf(page == HomePage.KnownHosts);
        TunnelsPage.Visibility = Ui.VisibleIf(page == HomePage.Tunnels);
        OverviewPage.Visibility = Ui.VisibleIf(page == HomePage.Overview);
    }

    // Ein Klick auf „Mehr“ wählt nichts aus, er klappt nur auf oder zu – ShowPage setzt die Auswahl danach zurück.
    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Pages.SelectedIndex < 0 || !Enum.IsDefined((HomeEntry)Pages.SelectedIndex))
        {
            return;
        }

        var entry = (HomeEntry)Pages.SelectedIndex;
        if (entry == HomeEntry.More)
        {
            // Derselbe Klick kommt danach noch als Tapped an – der soll nicht gleich wieder umklappen.
            _moreChosen = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _moreChosen = false);
        }

        Shell.ChooseHomeEntry(entry);
        ShowPage();
    }

    private bool _moreChosen;

    // Steht „Mehr“ schon hervorgehoben da (zugeklappt, eine Seite darunter ist offen), meldet die Liste beim Klick
    // keine neue Auswahl – dann klappt es hier auf.
    private void OnMoreTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (MoreEntry.IsSelected && !_moreChosen)
        {
            Shell.ChooseHomeEntry(HomeEntry.More);
        }
    }

    private void OnMoreKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (MoreEntry.IsSelected && !_moreChosen && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            Shell.ChooseHomeEntry(HomeEntry.More);
            e.Handled = true;
        }
    }

    // Die beiden Umschalter verhalten sich wie Optionsfelder: Ausschalten des gewählten nimmt ShowPage zurück.
    private void OnKeysToggled(object sender, RoutedEventArgs e) => Toggled(KeysToggle, HomePage.Keys);

    private void OnIdentitiesToggled(object sender, RoutedEventArgs e) => Toggled(IdentitiesToggle, HomePage.Identities);

    private void Toggled(ToggleButton toggle, HomePage page)
    {
        if (_filling)
        {
            return;
        }

        if (toggle.IsChecked == true)
        {
            Shell.HomePage = page;
        }

        ShowPage();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => Shell.IsSettingsOpen = true;

    /// <summary>Für den Selbsttest: die Menüs der Serverseite; nennt, was fehlt.</summary>
    internal string ExerciseMenus() => ServersPage.ExerciseMenus();
}
