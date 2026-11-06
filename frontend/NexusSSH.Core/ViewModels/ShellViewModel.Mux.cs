using System.Text;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.ViewModels;

/// <summary>Was mit laufenden tmux-/screen-Sitzungen eines Servers beim Verbinden geschehen soll.</summary>
public enum MuxStep
{
    /// <summary>Nichts von selbst.</summary>
    None,

    /// <summary>Nach dem Verbinden anbieten, wenn welche laufen.</summary>
    Offer,

    /// <summary>Nach dem Verbinden von selbst an die gemerkte Sitzung anhängen, wenn es sie gibt – sonst anbieten.</summary>
    Attach,
}

// tmux und screen: laufende Sitzungen des Servers finden und daran anhängen – auf Befehl oder, je Server
// gemerkt, gleich nach dem Verbinden.
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Der Server hat laufende Sitzungen, und der Benutzer soll wählen: die Sitzung der App und was dort läuft.
    /// Die Oberfläche zeigt den Dialog und meldet die Wahl mit <see cref="ChooseMux"/>.
    /// </summary>
    public event Action<SessionViewModel, IReadOnlyList<MuxSession>>? MuxOffered;

    /// <summary>Der gemerkte Schritt für ein Profil.</summary>
    public MuxStep MuxStepOf(ServerProfile profile)
        => Settings.MuxSteps.TryGetValue(profile.Id, out var step)
            ? step == MuxSteps.Offer ? MuxStep.Offer : MuxSteps.Target(step) is not null ? MuxStep.Attach : MuxStep.None
            : MuxStep.None;

    /// <summary>
    /// Sieht nach, was auf dem Server der aktiven Sitzung läuft, und bietet es an. Auf Befehl des Benutzers: Läuft
    /// nichts, sagt das die Ausgabe.
    /// </summary>
    public Task FindMuxAsync() => ActiveSession is { } session ? OfferMuxAsync(session, asked: true) : Task.CompletedTask;

    // Nach dem Verbinden: nur, wenn der Benutzer für diesen Server einen Schritt gemerkt hat.
    private void OnConnectedForMux(SessionViewModel session)
    {
        if (session is { State: ConnectionState.Connected, IsFilesOnly: false } && MuxStepOf(session.Profile) != MuxStep.None && _muxDone.Add(session))
        {
            _ = OfferMuxAsync(session, asked: false);
        }
        else if (session.State is ConnectionState.Closed)
        {
            // Die nächste Verbindung derselben Sitzung ist ein neuer Anfang.
            _muxDone.Remove(session);
        }
    }

    private readonly HashSet<SessionViewModel> _muxDone = [];

    private async Task OfferMuxAsync(SessionViewModel session, bool asked)
    {
        if (session.Inspector.Monitor is not { } monitor || session is not { State: ConnectionState.Connected, IsFilesOnly: false })
        {
            if (asked)
            {
                Log(L.Get("MuxUnavailable"), LogSeverity.Warning);
            }

            return;
        }

        IReadOnlyList<MuxSession> running;
        try
        {
            running = [.. (await monitor.MuxSessionsAsync()).Where(candidate => candidate.IsSafe)];
        }
        catch (RemoteFileException e)
        {
            if (asked)
            {
                Log(L.Get("MuxFailed", FailureText.Describe(e.Code, e.Message)), LogSeverity.Warning);
            }

            return;
        }

        // Unterdessen getrennt oder geschlossen: Dann gibt es nichts mehr, woran sich anhängen ließe.
        if (!Sessions.Contains(session) || session.State != ConnectionState.Connected)
        {
            return;
        }

        if (running.Count == 0)
        {
            if (asked)
            {
                Log(L.Get("MuxNone", Format.Visible(session.Profile.Name)));
            }

            return;
        }

        // Von selbst nur an genau die gemerkte Sitzung – gibt es sie nicht mehr, wird gefragt.
        if (!asked
            && Settings.MuxSteps.TryGetValue(session.Profile.Id, out var step)
            && MuxSteps.Target(step) is var (kind, name)
            && running.FirstOrDefault(candidate => candidate.Kind == kind && candidate.Name == name) is { } remembered)
        {
            Attach(session, remembered, MuxSteps.IsControl(step));
            return;
        }

        MuxOffered?.Invoke(session, running);
    }

    /// <summary>
    /// Die Wahl aus dem Dialog: an <paramref name="chosen"/> anhängen (oder, mit <c>null</c>, nichts tun) und für
    /// diesen Server merken, was künftig nach dem Verbinden geschieht.
    /// </summary>
    /// <param name="control">Bei tmux: im Steuermodus anhängen – die Fenster stehen dann als Tabs der App.</param>
    public void ChooseMux(SessionViewModel session, MuxSession? chosen, MuxStep step, bool control = false)
    {
        switch (step)
        {
            case MuxStep.Offer:
                Settings.MuxSteps[session.Profile.Id] = MuxSteps.Offer;
                break;
            case MuxStep.Attach when chosen is not null:
                Settings.MuxSteps[session.Profile.Id] = MuxSteps.For(chosen, control);
                break;
            default:
                Settings.MuxSteps.Remove(session.Profile.Id);
                break;
        }

        if (chosen is not null)
        {
            Attach(session, chosen, control);
        }
    }

    /// <summary>Ob für das Profil gemerkt ist, im Steuermodus von tmux anzuhängen.</summary>
    public bool MuxControlOf(ServerProfile profile) => Settings.MuxSteps.TryGetValue(profile.Id, out var step) && MuxSteps.IsControl(step);

    // Tippt die Zeile zum Anhängen in die Shell der Sitzung. Die Zeile hat der Kern gebaut; getippt wird sie nur,
    // wenn sie aus nichts als einem schlichten Namen besteht (MuxSession.IsSafe) – sie landet in der Shell des Servers.
    private void Attach(SessionViewModel session, MuxSession mux, bool control = false)
    {
        if (!mux.IsSafe || session is not { State: ConnectionState.Connected, IsFilesOnly: false })
        {
            return;
        }

        if (control && mux.Kind == "tmux")
        {
            AttachTmuxControl(session, mux);
            return;
        }

        session.Terminal.Write(Encoding.ASCII.GetBytes(mux.Command + "\r"));
        Log(L.Get("MuxAttached", Format.Visible(session.Profile.Name), mux.Kind, mux.Name));
    }
}
