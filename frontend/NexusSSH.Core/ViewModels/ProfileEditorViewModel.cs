using CommunityToolkit.Mvvm.ComponentModel;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.Terminals;

namespace NexusSSH.Core.ViewModels;

/// <summary>Auswahl im Feld „Sprung-Server“: ein anderes Profil, oder mit leerer Kennung die direkte Verbindung.</summary>
public sealed record JumpChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Auswahl im Feld „Identität“: eine Identität, oder ohne Kennung die eigenen Angaben des Profils.</summary>
/// <summary>Ein Eintrag der <c>~/.ssh/config</c>, dem ein Profil folgen kann; ohne <see cref="Alias"/>: „nicht verknüpft“.</summary>
public sealed record LinkChoice(string? Alias, string Name)
{
    public override string ToString() => Name;
}

public sealed record IdentityChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Auswahl im Feld „Farbschema“: ein Schema, oder ohne Kennung das allgemein eingestellte.</summary>
public sealed record ThemeChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Auswahl im Feld „Adressen“: nur IPv4, nur IPv6, oder ohne Kennung beide.</summary>
public sealed record AddressFamilyChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Formular zum Anlegen oder Ändern eines Serverprofils. Prüft dieselben Regeln wie der Kern
/// (<c>Profile::normalized</c> in <c>profiles.rs</c>), damit der Fehler schon beim Tippen sichtbar ist – das letzte
/// Wort hat trotzdem der Kern.
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly ServerProfile? _original;
    private readonly IReadOnlyList<Identity> _identities;

    /// <param name="profile">
    /// Das Profil, das geändert wird; <c>null</c> legt ein neues an. Ein Profil ohne Kennung ist eine Vorlage
    /// (Kopie eines anderen) – auch daraus entsteht ein neues.
    /// </param>
    /// <param name="profiles">Alle Profile – aus ihnen wird der Sprung-Server gewählt.</param>
    /// <param name="identities">Die Identitäten zur Wahl; ohne sie hat das Profil immer eigene Angaben.</param>
    /// <param name="themes">Die Farbschemata zur Wahl; ohne sie bleibt das Profil beim allgemein eingestellten.</param>
    /// <param name="configHosts">Die Einträge der <c>~/.ssh/config</c>, denen das Profil folgen kann.</param>
    /// <param name="localShells">Die Shells dieses Rechners für ein lokales Terminal (<see cref="LocalShells"/>).</param>
    public ProfileEditorViewModel(
        ServerProfile? profile = null,
        IReadOnlyList<ServerProfile>? profiles = null,
        IReadOnlyList<Identity>? identities = null,
        IReadOnlyList<SshConfigHost>? configHosts = null,
        IReadOnlyList<TerminalTheme>? themes = null,
        IReadOnlyList<LocalShell>? localShells = null)
    {
        _original = profile;
        _identities = identities ?? [];
        _configHosts = configHosts ?? [];

        // Der Eintrag, dem das Profil schon folgt, steht auch dann zur Wahl, wenn die Datei ihn gerade nicht nennt.
        var aliases = _configHosts.Select(host => host.Alias).ToList();
        if (profile?.SshAlias is { Length: > 0 } current && !aliases.Contains(current))
        {
            aliases.Add(current);
        }

        LinkChoices = BindableList.Of<LinkChoice>(
        [
            new LinkChoice(null, L.Get("ProfileLinkNone")),
            .. aliases.Select(alias => new LinkChoice(alias, alias)),
        ]);
        IdentityChoices = BindableList.Of<IdentityChoice>(
        [
            new IdentityChoice(null, L.Get("ProfileIdentityNone")),
            .. _identities.Select(identity => new IdentityChoice(identity.Id, $"{identity.Name}  ({identity.Summary})")),
        ]);

        ThemeChoices = BindableList.Of<ThemeChoice>(
        [
            new ThemeChoice(null, L.Get("ProfileThemeDefault")),
            .. (themes ?? []).Select(theme => new ThemeChoice(theme.Id, theme.Name)),
        ]);

        // Ein Schema, das es nicht mehr gibt, ist keines: Das Profil folgt dann wieder dem allgemeinen.
        Theme = ThemeChoices.FirstOrDefault(choice => choice.Id is not null && choice.Id == profile?.ThemeId) ?? ThemeChoices[0];

        Name = profile?.Name ?? string.Empty;
        Host = profile is { IsLocal: true } ? string.Empty : profile?.Host ?? string.Empty;
        Port = profile?.Port ?? 22;
        User = profile?.User ?? string.Empty;
        Group = profile?.Group ?? string.Empty;
        Tags = string.Join(", ", profile?.Tags ?? []);
        KeyPath = profile?.KeyPath ?? string.Empty;
        UseAgent = profile?.UseAgent ?? true;
        IdentitiesOnly = profile?.IdentitiesOnly ?? false;
        ForwardAgent = profile?.ForwardAgent ?? false;
        ForwardX11 = profile?.ForwardX11 ?? false;
        IsFavorite = profile?.IsFavorite ?? false;
        IsTelnet = profile?.IsTelnet ?? false;
        IsSerial = profile?.IsSerial ?? false;
        IsFtp = profile?.IsFtp ?? false;

        // Ein lokales Terminal: Die Shell steht im Profil als Host; was der Kern hier nicht (mehr) findet, bleibt
        // trotzdem zur Wahl – sonst wählte der Dialog beim Speichern still eine andere.
        List<LocalShell> shells = [.. (localShells ?? []).Where(shell => LocalShellNames.IsId(shell.Id))];
        if (profile is { IsLocal: true } && shells.TrueForAll(shell => shell.Id != profile.Host))
        {
            shells.Add(new LocalShell(profile.Host, LocalShellNames.Of(profile.Host)));
        }

        LocalShells = BindableList.Of(shells);
        LocalShellIndex = shells.FindIndex(shell => shell.Id == (profile is { IsLocal: true } ? profile.Host : LocalShellNames.Preferred(shells)?.Id));
        LocalDirectory = profile?.LocalDirectory ?? string.Empty;
        IsLocal = profile?.IsLocal ?? false;
        FtpPlain = profile?.IsPlainFtp ?? false;
        FtpImplicit = profile?.IsImplicitFtp ?? false;
        FtpActive = profile is { IsFtp: true, FtpActive: true };
        FtpEncodingIndex = profile is { IsFtp: true } ? Math.Max(0, Array.IndexOf(FtpEncodings, profile.FtpEncoding)) : 0;

        // Die Wahl bringt ihren üblichen Port mit – beim Laden gilt der des Profils.
        Port = profile?.Port ?? Port;
        Baud = profile?.Baud ?? ServerProfile.DefaultBaud;
        Notes = profile?.Notes ?? string.Empty;
        AutoReconnect = !(profile?.NoReconnect ?? false);
        AddressFamilies = BindableList.Of<AddressFamilyChoice>(
        [
            new AddressFamilyChoice(null, L.Get("ProfileAddressAny")),
            new AddressFamilyChoice("ipv4", L.Get("ProfileAddressIPv4")),
            new AddressFamilyChoice("ipv6", L.Get("ProfileAddressIPv6")),
        ]);
        AddressFamily = AddressFamilies.FirstOrDefault(choice => choice.Id == profile?.AddressFamily) ?? AddressFamilies[0];
        ConnectTimeout = profile?.ConnectTimeout ?? DefaultConnectTimeout;
        KeepAliveSeconds = profile?.KeepAliveSeconds ?? double.NaN;
        KeepAliveMissed = profile?.KeepAliveMissed ?? double.NaN;
        Term = profile?.Term ?? string.Empty;
        EnvText = ProfileEnv.Format(profile?.Env);
        CharsetIndex = Math.Max(0, Array.IndexOf(TerminalCharset.Ids, profile?.Charset));
        Mosh = profile?.Mosh ?? false;
        MoshPorts = profile?.MoshPorts ?? string.Empty;
        Compression = profile?.Compression ?? false;
        LegacyAlgorithms = profile?.LegacyAlgorithms ?? false;
        SshAlias = profile?.SshAlias;
        Colors = ProfileColors.All;
        Color = Colors.FirstOrDefault(color => color.Id.Length > 0 && color.Id == profile?.Color) ?? Colors[0];

        // Zur Wahl stehen alle anderen Profile, außer denen, die selbst über dieses hier springen – das gäbe
        // einen Kreis.
        var others = (profiles ?? []).Where(other => profile is null || (other.Id != profile.Id && !JumpsThrough(other, profile.Id, profiles ?? [])));
        List<JumpChoice> choices =
        [
            new JumpChoice(null, L.Get("ProfileJumpNone")),
            .. others.OrderBy(other => other.Name, StringComparer.CurrentCultureIgnoreCase).Select(other => new JumpChoice(other.Id, $"{other.Name}  ({other.Address})")),
        ];

        // Ein Sprung-Server, dessen Profil es nicht mehr gibt, bleibt sichtbar – sonst verschwände er beim
        // Speichern stillschweigend.
        if (profile?.JumpId is { } jump && choices.All(choice => choice.Id != jump))
        {
            choices.Add(new JumpChoice(jump, L.Get("ProfileJumpMissing")));
        }

        JumpChoices = BindableList.Of(choices);

        Jump = JumpChoices.First(choice => choice.Id == profile?.JumpId);

        // Zuletzt, weil die Wahl einer Identität Benutzer und Schlüssel setzt. Eine Identität, die es nicht mehr
        // gibt, ist keine: Das Profil hat dann wieder eigene Angaben.
        Identity = IdentityChoices.FirstOrDefault(choice => choice.Id is not null && choice.Id == profile?.IdentityId) ?? IdentityChoices[0];

        // Zuletzt, und ohne die Felder anzufassen: Hier wird nur gezeigt, was im Profil steht.
        _loading = true;
        Link = LinkChoices.FirstOrDefault(choice => choice.Alias is not null && choice.Alias == profile?.SshAlias) ?? LinkChoices[0];
        _loading = false;
    }

    /// <summary><c>true</c>, wenn ein neues Profil entsteht.</summary>
    public bool IsNew => _original is null or { Id.Length: 0 };

    public string Title => IsNew ? L.Get("ProfileNewTitle") : L.Get("ProfileEditTitle");

    public IReadOnlyList<JumpChoice> JumpChoices { get; }

    /// <summary>Die Identitäten zur Wahl; der erste Eintrag heißt „eigene Angaben“.</summary>
    public IReadOnlyList<IdentityChoice> IdentityChoices { get; }

    /// <summary>
    /// Woher Benutzer und Schlüssel kommen. Mit einer Identität zeigen die Felder deren Angaben und lassen sich
    /// hier nicht ändern (<see cref="HasOwnSignIn"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnSignIn), nameof(CanEditSignIn))]
    public partial IdentityChoice Identity { get; set; }

    /// <summary>Benutzer, Schlüssel und Agent gehören diesem Profil – nicht einer Identität.</summary>
    public bool HasOwnSignIn => Identity.Id is null;

    partial void OnIdentityChanged(IdentityChoice value)
    {
        if (_identities.FirstOrDefault(identity => identity.Id == value.Id) is { } identity)
        {
            (User, KeyPath, UseAgent) = (identity.User, identity.KeyPath ?? string.Empty, identity.UseAgent);
        }
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostError), nameof(IsValid))]
    public partial string Host { get; set; }

    /// <summary>double, weil die NumberBox so bindet; NaN heißt „leer“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortError), nameof(IsValid))]
    public partial double Port { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserError), nameof(IsValid))]
    public partial string User { get; set; }

    [ObservableProperty]
    public partial string Group { get; set; }

    /// <summary>Schlagworte, durch Komma getrennt.</summary>
    [ObservableProperty]
    public partial string Tags { get; set; }

    [ObservableProperty]
    public partial string KeyPath { get; set; }

    [ObservableProperty]
    public partial bool UseAgent { get; set; }

    /// <summary>
    /// Nur den genannten Schlüssel anbieten, keine weiteren aus dem Agenten – für Agenten und Passwort-Manager mit
    /// vielen Schlüsseln. Gilt nur, wenn ein Schlüssel genannt ist.
    /// </summary>
    [ObservableProperty]
    public partial bool IdentitiesOnly { get; set; }

    /// <summary>Die Kennfarben zur Wahl; die erste heißt „keine“.</summary>
    public IReadOnlyList<AccentColor> Colors { get; }

    /// <summary>Die gewählte Kennfarbe.</summary>
    [ObservableProperty]
    public partial AccentColor Color { get; set; }

    /// <summary>Die Farbschemata zur Wahl; der erste Eintrag heißt „wie eingestellt“.</summary>
    public IReadOnlyList<ThemeChoice> ThemeChoices { get; }

    /// <summary>Das Farbschema, in dem das Terminal dieses Servers erscheint – z. B. Produktion immer dunkelrot.</summary>
    [ObservableProperty]
    public partial ThemeChoice Theme { get; set; }

    /// <summary>Agent Forwarding – bewusst nie vorbelegt.</summary>
    [ObservableProperty]
    public partial bool ForwardAgent { get; set; }

    /// <summary>
    /// Das Terminal über Mosh führen (UDP, überlebt Funklöcher und Wechsel des Netzes). Nur SSH, nie über einen
    /// Sprung-Server (<see cref="CanUseMosh"/>); mit Mosh gibt es kein Agent- und kein X11-Forwarding und nur UTF-8 – die
    /// Felder dazu gehen beim Einschalten aus (<see cref="CanForwardAgent"/>, <see cref="CanChooseCharset"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool Mosh { get; set; }

    partial void OnMoshChanged(bool value)
    {
        if (value)
        {
            (ForwardAgent, ForwardX11, CharsetIndex) = (false, false, 0);
        }

        RaiseMoshChanged();
    }

    /// <summary>Die UDP-Ports für <c>mosh-server</c>: <c>60001</c> oder <c>60001:60010</c>; leer = er wählt selbst.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(MoshPortsError))]
    public partial string MoshPorts { get; set; }

    /// <summary>Mosh lässt sich wählen: bei SSH, ohne Sprung-Server.</summary>
    public bool CanUseMosh => IsSshProfile && Jump.Id is null;

    /// <summary>Mosh ist gewählt und gilt auch (nicht bei Telnet, FTP, seriell oder mit Sprung-Server).</summary>
    private bool UsesMosh => Mosh && CanUseMosh;

    /// <summary>Agent- und X11-Forwarding lassen sich wählen – mit Mosh gibt es beides nicht.</summary>
    public bool CanForwardAgent => !UsesMosh;

    /// <summary>Ein anderer Zeichensatz als UTF-8 lässt sich wählen – <c>mosh-server</c> spricht nur UTF-8.</summary>
    public bool CanChooseCharset => !UsesMosh;

    /// <summary>Der Abschnitt zu Mosh steht da: bei SSH.</summary>
    public bool ShowsMosh => IsSshProfile;

    /// <summary>Die Ports stehen nur da, solange Mosh gewählt ist.</summary>
    public bool ShowsMoshPorts => UsesMosh;

    /// <summary>Bei SSH, aber mit Sprung-Server: der Hinweis, warum Mosh nicht zu wählen ist.</summary>
    public bool ShowsMoshNoJump => IsSshProfile && !CanUseMosh;

    /// <summary>Agent Forwarding lässt sich wählen: nicht mit Mosh, und nicht, wenn die Datei von <c>~/.ssh/config</c> es bestimmt.</summary>
    public bool CanSetForwardAgent => CanEditAddress && !UsesMosh;

    /// <summary>Was an den Ports nicht stimmt; leer, wenn sie gelten oder Mosh nicht gewählt ist.</summary>
    public string MoshPortsError => UsesMosh && !Models.MoshPorts.IsValid(MoshPorts) ? L.Get("ProfileMoshPortsInvalid") : string.Empty;

    private void RaiseMoshChanged()
    {
        OnPropertyChanged(nameof(CanUseMosh));
        OnPropertyChanged(nameof(CanForwardAgent));
        OnPropertyChanged(nameof(CanSetForwardAgent));
        OnPropertyChanged(nameof(ShowsMosh));
        OnPropertyChanged(nameof(ShowsMoshPorts));
        OnPropertyChanged(nameof(ShowsMoshNoJump));
        OnPropertyChanged(nameof(CanChooseCharset));
        OnPropertyChanged(nameof(MoshPortsError));
        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>So lange wartet der Kern auf den Aufbau, wenn das Profil nichts sagt (Sekunden).</summary>
    public const int DefaultConnectTimeout = 15;

    private bool IsSshProfile => !IsTelnet && !IsSerial && !IsFtp && !IsLocal;

    /// <summary>Freier Text zum Server – Zweck, Ansprechpartner, Wartungsfenster. Nie ein Passwort: Die Notiz wandert im Export mit.</summary>
    [ObservableProperty]
    public partial string Notes { get; set; }

    /// <summary>Nach einem Abriss von selbst neu verbinden (sofern es in den Einstellungen an ist).</summary>
    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    /// <summary>Welche Adressen versucht werden: alle, nur IPv4 oder nur IPv6.</summary>
    public IReadOnlyList<AddressFamilyChoice> AddressFamilies { get; }

    [ObservableProperty]
    public partial AddressFamilyChoice AddressFamily { get; set; }

    /// <summary>Sekunden, die der Aufbau dauern darf (3–120).</summary>
    [ObservableProperty]
    public partial double ConnectTimeout { get; set; }

    /// <summary>
    /// Sekunden zwischen zwei Lebenszeichen an diesen Server (0 = keine); leer (<see cref="double.NaN"/>) = wie
    /// allgemein eingestellt.
    /// </summary>
    [ObservableProperty]
    public partial double KeepAliveSeconds { get; set; }

    /// <summary>So viele Lebenszeichen dürfen unbeantwortet bleiben; leer = wie allgemein eingestellt.</summary>
    [ObservableProperty]
    public partial double KeepAliveMissed { get; set; }

    /// <summary>
    /// Als welches Terminal sich die App bei diesem Server ausgibt (<c>TERM</c>); leer = <c>xterm-256color</c>. Für
    /// alte Systeme, die das nicht kennen (<c>vt100</c>, <c>xterm</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(TermError))]
    public partial string Term { get; set; }

    /// <summary>Umgebungsvariablen für die Shell, je Zeile <c>NAME=wert</c> (nur SSH).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(EnvError))]
    public partial string EnvText { get; set; }

    /// <summary>
    /// In welchem Zeichensatz der Server spricht, als Nummer der Auswahl in der Reihenfolge von
    /// <see cref="TerminalCharset.Ids"/>: 0 = UTF-8 (die Vorgabe).
    /// </summary>
    [ObservableProperty]
    public partial int CharsetIndex { get; set; }

    /// <summary>Was am Namen des Terminals nicht stimmt; leer, wenn er gilt (auch: leer gelassen).</summary>
    public string TermError => IsTermValid ? string.Empty : L.Get("ProfileTermInvalid");

    /// <summary>Welche Zeilen keine Umgebungsvariable sind; leer, wenn alle gelten.</summary>
    public string EnvError
    {
        get
        {
            ProfileEnv.Parse(EnvText, out var rejected);
            return rejected.Count == 0 ? string.Empty : L.Get("ProfileEnvInvalid", Format.Visible(string.Join(", ", rejected.Take(3))));
        }
    }

    private bool IsTermValid => Term.Trim().Length == 0 || ProfileEnv.IsTerminalName(Term.Trim());

    [ObservableProperty]
    public partial bool Compression { get; set; }

    private readonly IReadOnlyList<SshConfigHost> _configHosts;
    private bool _loading;

    /// <summary>Die Einträge der <c>~/.ssh/config</c> zur Wahl; der erste heißt „nicht verknüpft“.</summary>
    public IReadOnlyList<LinkChoice> LinkChoices { get; }

    /// <summary>
    /// Der Eintrag, dem das Profil folgt. Mit der Wahl zeigen die Felder gleich, was in der Datei steht – der Kern
    /// liest es beim Speichern, bei jedem Start und nach jeder Änderung der Datei selbst.
    /// </summary>
    [ObservableProperty]
    public partial LinkChoice Link { get; set; }

    partial void OnLinkChanged(LinkChoice value)
    {
        SshAlias = value.Alias;
        if (_loading || _configHosts.FirstOrDefault(host => host.Alias == value.Alias) is not { } host)
        {
            return;
        }

        (Host, Port, ForwardAgent) = (host.Host, host.Port, host.ForwardAgent);

        // Wie im Kern: Mit einer Identität bleiben Benutzer und Schlüssel deren Sache.
        if (HasOwnSignIn)
        {
            (User, KeyPath, IdentitiesOnly) = (host.User, host.KeyPath ?? string.Empty, host.IdentitiesOnly);
        }
    }

    /// <summary>Der Eintrag in <c>~/.ssh/config</c>, dem das Profil folgt; <c>null</c> = es gilt, was hier steht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked), nameof(LinkedText), nameof(CanEditAddress), nameof(CanEditPort), nameof(CanEditSignIn), nameof(CanSetForwardAgent))]
    public partial string? SshAlias { get; set; }

    public bool IsLinked => SshAlias is { Length: > 0 };

    public string LinkedText => L.Get("ProfileLinked", SshAlias ?? "");

    /// <summary>Verknüpfen lässt sich nur eine SSH-Verbindung, und nur, wenn es etwas zu wählen gibt.</summary>
    public bool CanLink => LinkChoices.Count > 1 && IsSshProfile;

    /// <summary>Was die Datei bestimmt, lässt sich hier nicht ändern – es würde beim nächsten Lesen überschrieben.</summary>
    public bool CanEditAddress => !IsLinked;

    public bool CanEditPort => IsNetwork && !IsLinked;

    /// <summary>Benutzer und Schlüssel: weder von einer Identität noch von der Datei bestimmt.</summary>
    public bool CanEditSignIn => HasOwnSignIn && !IsLinked;

    /// <summary>Löst die Verknüpfung: Ab dem Speichern gilt, was im Profil steht.</summary>
    public void Unlink() => Link = LinkChoices[0];

    /// <summary>Auch schwache, alte Verfahren anbieten – nie vorbelegt.</summary>
    [ObservableProperty]
    public partial bool LegacyAlgorithms { get; set; }

    /// <summary>X11-Forwarding – wie beim Agenten nie vorbelegt: Der Server erreicht damit den X-Server dieses Rechners.</summary>
    [ObservableProperty]
    public partial bool ForwardX11 { get; set; }

    /// <summary>Der Server, über den die Verbindung läuft; der erste Eintrag heißt „direkt“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseMosh))]
    public partial JumpChoice Jump { get; set; }

    // UDP geht nicht durch einen Sprung-Server: Wer einen wählt, hat Mosh danach nicht mehr.
    partial void OnJumpChanged(JumpChoice value)
    {
        if (value.Id is not null)
        {
            Mosh = false;
        }

        RaiseMoshChanged();
    }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>
    /// Telnet statt SSH – für Geräte, die nichts anderes sprechen. Unverschlüsselt; ein Benutzer ist dann keine
    /// Pflicht, weil man sich im Terminal selbst anmeldet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(CanLink))]
    public partial bool IsTelnet { get; set; }

    // Wer das Protokoll wechselt, meint fast immer auch dessen üblichen Port – außer er hat schon einen eigenen.
    partial void OnIsTelnetChanged(bool value)
    {
        RaiseMoshChanged();
        // Erst die anderen abwählen: Deren Port geht dabei auf 22 zurück, und von dort aus gilt der neue.
        if (value)
        {
            (IsSerial, IsFtp, IsLocal) = (false, false, false);
        }

        if (Port == (value ? 22 : 23))
        {
            Port = value ? 23 : 22;
        }
    }

    /// <summary>
    /// FTP statt SSH – nur Dateien, kein Terminal. Verschlüsselt mit TLS, solange <see cref="FtpPlain"/> nicht
    /// gewählt ist.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(CanLink), nameof(ShowsFtpPlainHint))]
    public partial bool IsFtp { get; set; }

    partial void OnIsFtpChanged(bool value)
    {
        RaiseMoshChanged();
        if (value)
        {
            (IsSerial, IsTelnet, IsLocal) = (false, false, false);
        }
        else
        {
            (FtpPlain, FtpImplicit, FtpActive) = (false, false, false);
        }

        if (Port == (value ? 22 : 21))
        {
            Port = value ? 21 : 22;
        }
    }

    /// <summary>
    /// FTP mit TLS ab dem ersten Byte (implizites FTPS, üblich auf Port 990) statt mit <c>AUTH TLS</c>. Genauso
    /// verschlüsselt und geprüft – nur der Anfang ist ein anderer.
    /// </summary>
    [ObservableProperty]
    public partial bool FtpImplicit { get; set; }

    partial void OnFtpImplicitChanged(bool value)
    {
        if (value)
        {
            FtpPlain = false;
        }

        if (IsFtp && Port == (value ? 21 : 990))
        {
            Port = value ? 990 : 21;
        }
    }

    /// <summary>
    /// Aktiver Modus: Der Server baut die Datenverbindungen zu diesem Rechner auf. Nie vorbelegt – hinter einem
    /// Router oder einer Firewall kommt er meist nicht durch, und die App lauscht dafür auf einem Port.
    /// </summary>
    [ObservableProperty]
    public partial bool FtpActive { get; set; }

    // Die Auswahl im Dialog steht in dieser Reihenfolge: UTF-8, ISO-8859-1, Windows-1252.
    private static readonly string?[] FtpEncodings = [null, ServerProfile.FtpLatin1, ServerProfile.FtpWindows1252];

    /// <summary>
    /// Wie der FTP-Server Dateinamen schreibt, als Nummer der Auswahl: 0 = UTF-8 (die Vorgabe), 1 = ISO-8859-1,
    /// 2 = Windows-1252 – für alte Server, deren Umlaute sonst als Fragezeichen erscheinen.
    /// </summary>
    [ObservableProperty]
    public partial int FtpEncodingIndex { get; set; }

    partial void OnFtpPlainChanged(bool value)
    {
        if (value)
        {
            FtpImplicit = false;
        }
    }

    /// <summary>FTP ohne Verschlüsselung – nie vorbelegt: Passwort und Dateien gehen dann im Klartext.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFtpPlainHint))]
    public partial bool FtpPlain { get; set; }

    /// <summary>Die Warnung steht nur da, solange unverschlüsseltes FTP gewählt ist.</summary>
    public bool ShowsFtpPlainHint => IsFtp && FtpPlain;

    /// <summary>
    /// Eine serielle Schnittstelle dieses Rechners statt einer Verbindung im Netz: Im Feld „Host“ steht sie
    /// (<c>COM3</c>), <see cref="Baud"/> nennt die Geschwindigkeit. Einen Port und einen Benutzer braucht es nicht.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(HostError), nameof(IsNetwork), nameof(BaudError), nameof(CanEditPort), nameof(CanLink))]
    public partial bool IsSerial { get; set; }

    partial void OnIsSerialChanged(bool value)
    {
        RaiseMoshChanged();
        if (value)
        {
            (IsTelnet, IsFtp, IsLocal) = (false, false, false);
        }
    }

    /// <summary>
    /// Eine Shell dieses Rechners statt eines Servers (<see cref="ServerProfile.IsLocal"/>): Gewählt wird sie aus
    /// <see cref="LocalShells"/>, dazu gibt es einen Startordner. Benutzer, Port, Schlüssel und alles, was eine
    /// Verbindung beschreibt, entfallen – das Profil bleibt auf diesem Rechner (nicht im Export).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(HostError), nameof(IsNetwork), nameof(CanEditPort), nameof(CanLink), nameof(LocalFolderError), nameof(IsRemote))]
    public partial bool IsLocal { get; set; }

    /// <summary>
    /// Das Profil beschreibt etwas, das hinter einer Adresse liegt (Host, Benutzer, Anmeldung, Weg, Verbindung) –
    /// alles, was ein lokales Terminal nicht hat. Der Dialog blendet es mit diesem Schalter aus.
    /// </summary>
    public bool IsRemote => !IsLocal;

    partial void OnIsLocalChanged(bool value)
    {
        RaiseMoshChanged();
        if (value)
        {
            (IsTelnet, IsSerial, IsFtp) = (false, false, false);
        }
    }

    /// <summary>
    /// Die Shells dieses Rechners, wie der Kern sie fand – ohne Pfad. Eine, die das Profil nennt und der Kern nicht
    /// mehr findet, steht trotzdem darin.
    /// </summary>
    public IReadOnlyList<LocalShell> LocalShells { get; }

    /// <summary>
    /// Die gewählte Shell als Nummer in <see cref="LocalShells"/> (die ComboBox bindet so); −1 = keine, etwa weil der
    /// Kern keine fand.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalShellId), nameof(IsValid))]
    public partial int LocalShellIndex { get; set; }

    /// <summary>Die Kennung der gewählten Shell – das, was im Profil als <see cref="ServerProfile.Host"/> steht.</summary>
    public string LocalShellId
    {
        get => LocalShellIndex >= 0 && LocalShellIndex < LocalShells.Count ? LocalShells[LocalShellIndex].Id : string.Empty;
        set
        {
            for (var index = 0; index < LocalShells.Count; index++)
            {
                if (LocalShells[index].Id == value)
                {
                    LocalShellIndex = index;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Der Ordner, in dem die Shell beginnt; leer = der Benutzerordner. Absolut und schlicht wie im Kern; ob es ihn
    /// gibt, sieht erst der Start nach (fehlt er, beginnt die Shell trotzdem im Benutzerordner).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalFolderError), nameof(IsValid))]
    public partial string LocalDirectory { get; set; }

    public string LocalFolderError => IsLocal && !IsLocalFolderValid ? L.Get("ProfileLocalFolderInvalid") : string.Empty;

    private bool IsLocalFolderValid => LocalDirectory.Trim().Length == 0 || LocalShellNames.IsFolder(LocalDirectory.Trim());

    /// <summary>Über das Netz (SSH oder Telnet) – nur dann zählt der Port.</summary>
    public bool IsNetwork => !IsSerial && !IsLocal;

    /// <summary>Geschwindigkeit der seriellen Verbindung. double, weil die NumberBox so bindet; NaN heißt „leer“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(BaudError))]
    public partial double Baud { get; set; } = ServerProfile.DefaultBaud;

    public string BaudError => !IsSerial || IsBaudValid ? string.Empty : L.Get("ProfileBaudInvalid");

    private bool IsBaudValid => Baud is >= 50 and <= 4_000_000 && Baud == Math.Floor(Baud);

    /// <summary>Wie der Kern (<c>serial.rs</c>): <c>COM</c> und eine Nummer, sonst nichts.</summary>
    private static bool IsSerialPort(string name)
        => name.Length is >= 4 and <= 6
            && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && name[3..].All(char.IsAsciiDigit)
            && name[3..] != "0";

    /// <summary>Fehlertext zum Host; leer, wenn er passt oder noch nichts eingegeben wurde.</summary>
    public string HostError => IsLocal || Host.Length == 0 || IsHostValid
        ? string.Empty
        : IsSerial ? L.Get("ProfileSerialPortInvalid") : L.Get("ProfileHostInvalid");

    public string UserError => User.Length > 0 && !IsUserValid ? L.Get("ProfileUserInvalid") : string.Empty;

    public string PortError => IsPortValid ? string.Empty : L.Get("ProfilePortInvalid");

    public bool IsValid => IsLocal
        ? LocalShellNames.IsId(LocalShellId) && IsLocalFolderValid
        : IsHostValid && IsUserValid && IsPortValid && (!IsSerial || IsBaudValid) && IsTermValid && EnvError.Length == 0 && MoshPortsError.Length == 0;

    private bool IsHostValid
    {
        get
        {
            var host = Host.Trim();
            return IsSerial
                ? IsSerialPort(host)
                : host.Length > 0 && !host.StartsWith('-') && !host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        }
    }

    private bool IsUserValid
    {
        get
        {
            var user = User.Trim();
            return (user.Length > 0 || IsTelnet || IsSerial) && !user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        }
    }

    private bool IsPortValid => Port is >= 1 and <= 65535 && Port == Math.Floor(Port);

    /// <summary>Das Profil aus den Eingaben. Kennung und „zuletzt verbunden“ bleiben vom Original.</summary>
    public ServerProfile ToProfile()
    {
        var host = IsLocal ? LocalShellId : Host.Trim();
        var key = IsLocal ? string.Empty : KeyPath.Trim().Trim('"');

        // Mit Mosh gibt es weder Agent noch X11 noch einen anderen Zeichensatz – der Kern schaltet sie ebenso ab.
        var mosh = UsesMosh;
        return new ServerProfile(
            _original?.Id ?? string.Empty,
            Name.Trim().Length > 0 ? Name.Trim() : IsLocal ? LocalShellNames.Of(host, LocalShells.FirstOrDefault(shell => shell.Id == host)?.Name) : host,
            host,
            IsLocal ? 22 : (int)Port,
            IsLocal ? string.Empty : User.Trim(),
            Group.Trim(),
            [.. Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            IsFavorite,
            key.Length > 0 ? key : null,
            UseAgent,
            _original?.LastConnected,
            IsFtp || IsLocal ? null : Jump.Id,
            ForwardAgent && !mosh,
            Color: Color.Id.Length > 0 ? Color.Id : null,
            IdentityId: IsSshProfile ? Identity.Id : null,
            Protocol: IsSerial ? ServerProfile.Serial : IsTelnet ? ServerProfile.Telnet : IsFtp ? ServerProfile.Ftp : IsLocal ? ServerProfile.Local : null,
            ForwardX11: ForwardX11 && IsSshProfile && !mosh,
            // Das erkannte System gehört zum Server: Zeigt das Profil auf einen anderen, wird neu erkannt.
            System: _original is { } original && original.Host == host ? original.System : null,
            Baud: IsSerial ? (int)Baud : null,
            Notes: Notes.Trim(),
            NoReconnect: !AutoReconnect,
            AddressFamily: IsSerial || IsLocal ? null : AddressFamily.Id,
            // Die übliche Zeit steht nicht im Profil: So folgt es einer Vorgabe, die sich einmal ändert.
            ConnectTimeout: !IsSerial && !IsLocal && ConnectTimeout is >= 3 and <= 120 && (int)ConnectTimeout != DefaultConnectTimeout ? (int)ConnectTimeout : null,
            Compression: Compression && IsSshProfile,
            LegacyAlgorithms: LegacyAlgorithms && IsSshProfile,
            SshAlias: IsSshProfile ? SshAlias : null,
            FtpTls: !IsFtp ? null : FtpPlain ? ServerProfile.FtpPlain : FtpImplicit ? ServerProfile.FtpImplicit : null,
            // Ohne Terminal (FTP) gibt es nichts zu färben.
            ThemeId: IsFtp ? null : Theme.Id,
            FtpActive: IsFtp && FtpActive,
            FtpEncoding: IsFtp && FtpEncodingIndex >= 0 && FtpEncodingIndex < FtpEncodings.Length ? FtpEncodings[FtpEncodingIndex] : null,
            // Die Tunnel pflegt ihre eigene Seite; der Dialog reicht sie durch.
            Tunnels: IsLocal ? null : _original?.Tunnels,
            // Lebenszeichen kennt nur SSH; ein leeres Feld heißt „wie allgemein“.
            KeepAliveSeconds: IsSshProfile && double.IsFinite(KeepAliveSeconds) && AppSettings.IsKeepAliveSeconds((int)KeepAliveSeconds) ? (int)KeepAliveSeconds : null,
            KeepAliveMissed: IsSshProfile && double.IsFinite(KeepAliveMissed) && AppSettings.IsKeepAliveMissed((int)KeepAliveMissed) ? (int)KeepAliveMissed : null,
            // Ein Terminal nennen SSH und Telnet; Variablen kennt nur SSH; der Zeichensatz gilt jedem Terminal.
            Term: !IsSerial && !IsFtp && !IsLocal && Term.Trim() is { Length: > 0 } term && term != ProfileEnv.DefaultTerm ? term : null,
            Env: IsSshProfile && ProfileEnv.Parse(EnvText, out _) is { Count: > 0 } env ? env : null,
            Charset: !IsFtp && !IsLocal && !mosh && CharsetIndex > 0 && CharsetIndex < TerminalCharset.Ids.Length ? TerminalCharset.Ids[CharsetIndex] : null,
            IdentitiesOnly: IsSshProfile && IdentitiesOnly && KeyPath.Trim().Length > 0,
            LocalDirectory: IsLocal && LocalDirectory.Trim() is { Length: > 0 } folder ? folder : null,
            Mosh: mosh,
            MoshPorts: mosh ? Models.MoshPorts.Clean(MoshPorts) : null);
    }

    // Führt der Weg von `start` über Sprung-Server irgendwann zu `target`?
    private static bool JumpsThrough(ServerProfile start, string target, IReadOnlyList<ServerProfile> profiles)
    {
        var current = start;
        for (var steps = 0; steps <= profiles.Count; steps++)
        {
            if (current.JumpId is not { } next)
            {
                return false;
            }

            if (next == target)
            {
                return true;
            }

            if (profiles.FirstOrDefault(profile => profile.Id == next) is not { } following)
            {
                return false;
            }

            current = following;
        }

        return true;
    }
}
