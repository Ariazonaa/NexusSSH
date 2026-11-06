using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using NexusSSH.Core;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.App.Dialogs;

// Der Verlauf der Auslastung über die ganze Sitzung: zwei Kurven, ein wählbarer Zeitraum, Export als CSV.
public sealed partial class DialogService
{
    private const double ChartWidth = 480, ChartHeight = 96;

    /// <summary>Zeigt den Verlauf von CPU und Arbeitsspeicher einer Sitzung; solange der Dialog offen ist, wächst er mit.</summary>
    public async Task ShowLoadHistoryAsync(SessionViewModel session, Microsoft.UI.WindowId window)
    {
        var history = session.Inspector.History;
        TimeSpan?[] ranges = [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), null];
        var range = new ComboBox
        {
            Header = L.Get("LoadHistoryRange"),
            ItemsSource = new List<string> { L.Get("LoadHistoryRange5"), L.Get("LoadHistoryRange60"), L.Get("LoadHistoryRangeAll") },
            SelectedIndex = history.Count > 0 && DateTimeOffset.Now - history.Samples[0].Time > TimeSpan.FromMinutes(5) ? 2 : 0,
            MinWidth = 200,
        };
        var (cpu, cpuValue) = Chart(L.Get("Cpu"));
        var (memory, memoryValue) = Chart(L.Get("Memory"));
        var span = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = L.Get("LoadHistorySaveCsv") };

        void Draw()
        {
            var now = DateTimeOffset.Now;
            var chosen = ranges[Math.Max(range.SelectedIndex, 0)];
            Fill(cpu, history.Lines(chosen, now, sample => sample.Cpu, ChartWidth, ChartHeight));
            Fill(memory, history.Lines(chosen, now, sample => sample.Memory, ChartWidth, ChartHeight));
            var shown = history.Within(chosen, now);
            save.IsEnabled = history.Count > 0;
            if (shown.Count == 0)
            {
                (cpuValue.Text, memoryValue.Text) = (string.Empty, string.Empty);
                span.Text = L.Get("LoadHistoryEmpty");
                return;
            }

            // Rechts neben dem Namen: der höchste Wert des Zeitraums – danach sucht man in einem Verlauf zuerst.
            cpuValue.Text = L.Get("LoadHistoryPeak", Format.Percent(shown.Max(sample => sample.Cpu)));
            memoryValue.Text = L.Get("LoadHistoryPeak", Format.Percent(shown.Max(sample => sample.Memory)));
            span.Text = L.Get("LoadHistorySpan", shown.Count, shown[0].Time.ToLocalTime().ToString("T", L.Culture), shown[^1].Time.ToLocalTime().ToString("T", L.Culture));
        }

        void OnMeasured(ServerStats stats) => Draw();
        range.SelectionChanged += (_, _) => Draw();
        save.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker(window)
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = LoadHistoryFile.Name(session.Profile.Name, DateTimeOffset.Now),
                    DefaultFileExtension = ".csv",
                };
                picker.FileTypeChoices.Add(L.Get("LoadHistoryCsvFiles"), [".csv"]);
                if (await picker.PickSaveFileAsync() is { } file)
                {
                    App.Shell.SaveLoadHistory(session, file.Path);
                }
            }
            catch (Exception exception)
            {
                App.WriteCrashLog(exception);
            }
        };

        var dialog = new ContentDialog
        {
            Title = L.Get("LoadHistoryTitle", Format.Visible(session.Title)),
            Content = new StackPanel { Width = ChartWidth, Spacing = 10, Children = { range, cpu, memory, span, save } },
            CloseButtonText = L.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };

        Draw();
        session.Inspector.Measured += OnMeasured;
        try
        {
            await ShowAsync(dialog, CancellationToken.None);
        }
        finally
        {
            session.Inspector.Measured -= OnMeasured;
        }
    }

    // Eine Fläche für eine Kurve mit ihrem Namen darüber; die Linien kommen mit Fill hinein.
    private static (StackPanel Panel, TextBlock Value) Chart(string name)
    {
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        var canvas = new Canvas { Width = ChartWidth, Height = ChartHeight };
        var frame = new Border
        {
            Child = canvas,
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(canvas, name);
        var header = new Grid { Children = { new TextBlock { Text = name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] }, value } };
        return (new StackPanel { Spacing = 4, Children = { header, frame } }, value);
    }

    private static void Fill(StackPanel chart, IReadOnlyList<IReadOnlyList<(double X, double Y)>> lines)
    {
        var canvas = (Canvas)((Border)chart.Children[1]).Child;
        canvas.Children.Clear();
        var stroke = Ui.ChartBrush;
        foreach (var line in lines)
        {
            var points = new PointCollection();
            foreach (var (x, y) in line)
            {
                // Am Rand bliebe von einer Linie nur die halbe Breite.
                points.Add(new Windows.Foundation.Point(x, Math.Clamp(y, 1, ChartHeight - 1)));
            }

            // Eine einzelne Messung ist kein Strich: ein kurzer waagerechter, damit sie zu sehen ist.
            if (points.Count == 1)
            {
                points.Add(new Windows.Foundation.Point(points[0].X + 2, points[0].Y));
            }

            canvas.Children.Add(new Polyline { Points = points, Stroke = stroke, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round });
        }
    }
}
