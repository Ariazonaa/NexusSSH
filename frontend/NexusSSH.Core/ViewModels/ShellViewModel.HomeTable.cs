using NexusSSH.Core.Models;
using NexusSSH.Core.Settings;

namespace NexusSSH.Core.ViewModels;

// Die Startseite als Tabelle: alle Server in einer Liste, nach einer Spalte sortiert – für viele Server
// übersichtlicher als Kacheln. Der Filter über der Seite gilt hier genauso; Gruppen sind hier nur eine Spalte.
public sealed partial class ShellViewModel
{
    /// <summary>Tabelle statt Kacheln auf der Startseite.</summary>
    public bool HomeAsTable
    {
        get => Settings.HomeAsTable;
        set
        {
            if (value != Settings.HomeAsTable)
            {
                Settings.HomeAsTable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNoTableMatches));
            }
        }
    }

    /// <summary>Die Spalte, nach der die Tabelle sortiert ist.</summary>
    public HomeTableSort HomeTableSort => Settings.HomeTableSort;

    public bool HomeTableDescending => Settings.HomeTableDescending;

    /// <summary>
    /// Alle Profile, die der Filter übrig lässt, in der Reihenfolge der gewählten Spalte. Bei Gleichstand entscheidet
    /// der Name; wer noch nie verbunden war, steht bei „zuletzt“ am Ende – in beiden Richtungen.
    /// </summary>
    public IReadOnlyList<ServerProfile> HomeTable
    {
        get
        {
            var words = HomeFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var shown = _profiles.Where(profile => words.All(word => Matches(profile, word)));
            var text = StringComparer.CurrentCultureIgnoreCase;
            IOrderedEnumerable<ServerProfile> sorted = (HomeTableSort, HomeTableDescending) switch
            {
                (HomeTableSort.Address, false) => shown.OrderBy(profile => profile.Host, text).ThenBy(profile => profile.Port),
                (HomeTableSort.Address, true) => shown.OrderByDescending(profile => profile.Host, text).ThenByDescending(profile => profile.Port),
                (HomeTableSort.Group, false) => shown.OrderBy(profile => profile.Group, text),
                (HomeTableSort.Group, true) => shown.OrderByDescending(profile => profile.Group, text),
                (HomeTableSort.Recent, false) => shown.OrderBy(profile => profile.LastConnected is null).ThenBy(profile => profile.LastConnected),
                (HomeTableSort.Recent, true) => shown.OrderBy(profile => profile.LastConnected is null).ThenByDescending(profile => profile.LastConnected),
                (_, true) => shown.OrderByDescending(profile => profile.Name, text),
                _ => shown.OrderBy(profile => profile.Name, text),
            };
            return BindableList.Of(sorted.ThenBy(profile => profile.Name, text));
        }
    }

    /// <summary>Es gibt Profile, aber der Filter lässt in der Tabelle keines übrig.</summary>
    public bool HasNoTableMatches => HomeAsTable && HasProfiles && HomeTable.Count == 0;

    /// <summary>
    /// Ein Klick auf einen Spaltenkopf: eine andere Spalte sortiert aufsteigend („zuletzt“: die jüngste zuerst),
    /// dieselbe noch einmal dreht die Richtung um.
    /// </summary>
    public void SortHomeTable(HomeTableSort column)
    {
        if (column == Settings.HomeTableSort)
        {
            Settings.HomeTableDescending = !Settings.HomeTableDescending;
        }
        else
        {
            Settings.HomeTableSort = column;
            Settings.HomeTableDescending = column == HomeTableSort.Recent;
        }

        OnPropertyChanged(nameof(HomeTableSort));
        OnPropertyChanged(nameof(HomeTableDescending));
        HomeTableChanged();
    }

    // Die Profile, der Filter oder die Sortierung haben sich geändert.
    private void HomeTableChanged()
    {
        OnPropertyChanged(nameof(HomeTable));
        OnPropertyChanged(nameof(HasNoTableMatches));
    }
}
