namespace NexusSSH.Core.Settings;

/// <summary>
/// Wo das Fenster wieder aufgeht. Ist die Titelleiste auf dem Bildschirm zu greifen, bleibt es, wo es war, auch wenn es
/// über den Rand oder auf einen zweiten Bildschirm ragt. Sonst (der Bildschirm von damals fehlt) rückt es herein: Ein
/// Fenster, das niemand sieht, ließe sich mit der Maus nicht zurückholen.
/// </summary>
public static class WindowPlacement
{
    // So hoch ist die Titelleiste, und so viel von ihr muss zu sehen sein, um sie zu greifen.
    private const int TitleHeight = 48;
    private const int Grip = 120;

    /// <param name="window">Der gemerkte Platz.</param>
    /// <param name="work">Die Arbeitsfläche des nächstgelegenen Bildschirms, ohne Taskleiste.</param>
    public static WindowBounds Fit(WindowBounds window, WindowBounds work)
    {
        var visible = Math.Min(window.X + window.Width, work.X + work.Width) - Math.Max(window.X, work.X);
        if (visible >= Grip && window.Y >= work.Y && window.Y <= work.Y + work.Height - TitleHeight)
        {
            return window;
        }

        var width = Math.Min(window.Width, work.Width);
        var height = Math.Min(window.Height, work.Height);
        return new WindowBounds(
            Math.Clamp(window.X, work.X, work.X + work.Width - width),
            Math.Clamp(window.Y, work.Y, work.Y + work.Height - height),
            width,
            height);
    }
}
