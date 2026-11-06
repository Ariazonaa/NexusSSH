using Microsoft.UI.Xaml.Controls;

namespace NexusSSH.App.Views;

public sealed partial class DualFilesView : UserControl
{
    public DualFilesView()
    {
        InitializeComponent();

        // Die Lücke zwischen den Spalten ist die zwischen allen Karten.
        Columns.ColumnSpacing = Ui.Gap;
        LocalPane.RemoteSelection = () => RemotePane.SelectedEntries;
        LocalPane.SyncRequested = RemotePane.SyncWithAsync;
    }

    /// <summary>Setzt den Fokus in die Liste des Servers – die Ansicht steht an der Stelle des Terminals.</summary>
    public bool FocusList() => RemotePane.FocusList() || LocalPane.FocusList();

    /// <summary>Für den Selbsttest: die Spaltenköpfe beider Listen einmal bedienen.</summary>
    internal void ExerciseColumnHeaders()
    {
        RemotePane.ExerciseColumnHeaders();
        LocalPane.ExerciseColumnHeaders();
    }
}
