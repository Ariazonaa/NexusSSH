using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NexusSSH.Core.ViewModels;

/// <summary>
/// Die Tabs, wie eine Reiterleiste sie hält – eine eigene Liste neben <see cref="ShellViewModel.Sessions"/>, damit
/// sich die Reiter mit der Maus umsortieren lassen.
/// <para>
/// Eine Reiterleiste verschiebt nicht: Sie nimmt den gezogenen Reiter aus ihrer Liste und setzt ihn woanders
/// wieder ein. Täte sie das mit der Sitzungsliste selbst, sähe es für einen Augenblick aus, als sei die Sitzung
/// geschlossen – ihr Terminal würde samt Verlauf abgebaut. Deshalb bekommt die Leiste diese Liste, und erst das
/// Ergebnis wird übernommen: als Verschieben (<see cref="ShellViewModel.MoveTab"/>). Die Reihenfolge gehört
/// weiter der Sitzungsliste; alles, was sich dort ändert, wird hierher nachgezogen.
/// </para>
/// </summary>
public sealed class TabStrip
{
    private readonly ShellViewModel _shell;
    private readonly Action<Action> _later;

    // Der Reiter, den die Leiste gerade herausgenommen hat, um ihn woanders einzusetzen.
    private SessionViewModel? _lifted;

    // Die Änderung an Tabs kommt von hier, nicht von der Leiste.
    private bool _following;

    /// <param name="later">
    /// Führt etwas aus, sobald das laufende Ereignis vorbei ist. Eine Liste lässt sich nicht ändern, während sie
    /// ihren Zuhörern gerade eine Änderung meldet – und die Reiterleiste hört zu. Ohne Angabe: sofort (Tests).
    /// </param>
    public TabStrip(ShellViewModel shell, Action<Action>? later = null)
    {
        _shell = shell;
        _later = later ?? (action => action());
        Tabs = [.. shell.Sessions];
        shell.Sessions.CollectionChanged += OnSessionsChanged;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>Die Reiter in ihrer Reihenfolge – die Liste für die Reiterleiste.</summary>
    public ObservableCollection<SessionViewModel> Tabs { get; }

    /// <summary>
    /// Ob die Leiste gerade einen Reiter umsetzt. Solange fehlt er in <see cref="Tabs"/>, und was die Leiste
    /// währenddessen auswählt, gilt nicht.
    /// </summary>
    public bool IsReordering => _lifted is not null;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_following)
        {
            return;
        }

        _following = true;
        try
        {
            switch (e)
            {
                case { Action: NotifyCollectionChangedAction.Add, NewItems: [SessionViewModel added] } when _lifted is null && e.NewStartingIndex <= Tabs.Count:
                    Tabs.Insert(e.NewStartingIndex, added);
                    break;

                case { Action: NotifyCollectionChangedAction.Remove } when _lifted is null && e.OldStartingIndex < Tabs.Count:
                    Tabs.RemoveAt(e.OldStartingIndex);
                    break;

                case { Action: NotifyCollectionChangedAction.Move } when _lifted is null && e.OldStartingIndex < Tabs.Count && e.NewStartingIndex < Tabs.Count:
                    Tabs.Move(e.OldStartingIndex, e.NewStartingIndex);
                    break;
            }

            // Ändern sich die Sitzungen, während ein Reiter herausgenommen ist, gilt das Umsetzen nicht mehr.
            _lifted = null;
            Repair();
        }
        finally
        {
            _following = false;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_following)
        {
            return;
        }

        _following = true;
        try
        {
            switch (e)
            {
                case { Action: NotifyCollectionChangedAction.Remove, OldItems: [SessionViewModel lifted] } when _lifted is null:
                    _lifted = lifted;
                    return;

                case { Action: NotifyCollectionChangedAction.Add, NewItems: [SessionViewModel placed] } when ReferenceEquals(placed, _lifted):
                    _lifted = null;
                    _shell.MoveTab(placed, e.NewStartingIndex);
                    break;
            }

            // Alles andere steht der Leiste nicht zu: Tabs öffnet und schließt nur die Shell.
            _lifted = null;
        }
        finally
        {
            _following = false;
        }

        _later(Restore);
    }

    private void Restore()
    {
        if (_lifted is not null)
        {
            return;
        }

        _following = true;
        try
        {
            Repair();
        }
        finally
        {
            _following = false;
        }
    }

    // Stimmt die Liste nicht mit den Sitzungen überein, wird sie neu aufgebaut – lieber einmal zu oft als ein
    // Reiter ohne Sitzung.
    private void Repair()
    {
        if (Tabs.SequenceEqual(_shell.Sessions))
        {
            return;
        }

        Tabs.Clear();
        foreach (var session in _shell.Sessions)
        {
            Tabs.Add(session);
        }
    }
}
