using NexusSSH.Core.Localization;
using NexusSSH.Core.Models;

namespace NexusSSH.Core.Services;

/// <summary>
/// Wie die App eine Shell dieses Rechners nennt. Die Namen des Kerns sind englisch; übersetzt werden hier nur
/// die, die einen deutschen (französischen, spanischen) Namen haben – die Eingabeaufforderung und die
/// Anmelde-Shell. Alles andere (PowerShell, WSL, Git Bash, <c>zsh</c>) heißt überall gleich. Der Name kann eine
/// Distribution von WSL oder eine Datei aus <c>/etc/shells</c> nennen und ist deshalb fremder Text: gezeigt wird er
/// nur über <see cref="Format.Visible"/>.
/// </summary>
public static class LocalShellNames
{
    /// <summary>Mehr Zeichen hat die Kennung einer Shell nie (wie im Kern: <c>local::valid_id</c>).</summary>
    public const int MaxIdLength = 255;

    private const int MaxDistributionLength = 64;

    private static readonly string[] Kinds = ["pwsh", "powershell", "cmd", "wsl", "gitbash", "login"];

    /// <summary>
    /// Ob das eine Kennung sein kann, die der Kern annimmt: eine der festen Arten, <c>wsl:</c> mit dem Namen einer
    /// Distribution, und außerhalb von Windows ein schlichter absoluter Pfad (eine Shell aus <c>/etc/shells</c>).
    /// Ob es die Shell hier gibt, weiß nur der Kern – er hat das letzte Wort wie bei jedem Profil.
    /// </summary>
    public static bool IsId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        if (Array.IndexOf(Kinds, id) >= 0)
        {
            return true;
        }

        if (id.StartsWith("wsl:", StringComparison.Ordinal))
        {
            var name = id[4..];
            return name.Length is >= 1 and <= MaxDistributionLength
                && name[0] is not ('.' or '-')
                && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
        }

        return !OperatingSystem.IsWindows() && IsPath(id);
    }

    // Ein absoluter Pfad ohne Leerzeichen, ohne `.` und `..` – so steht eine Shell in /etc/shells.
    private static bool IsPath(string path)
        => path[0] == '/'
            && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '+' or '-')
            && !path.Split('/').Any(part => part is "." or "..");

    /// <summary>
    /// Der Name, wie er in der Oberfläche steht. <paramref name="coreName"/> ist der Name, den der Kern zur Kennung
    /// geliefert hat; ohne ihn wird er aus der Kennung gebildet (ein Profil kennt nur diese).
    /// </summary>
    public static string Of(string id, string? coreName = null)
    {
        var named = string.IsNullOrWhiteSpace(coreName) ? null : Format.Visible(coreName.Trim());
        return id switch
        {
            "cmd" => L.Get("LocalShellCmd"),
            "login" => named is null ? L.Get("LocalShellLoginPlain") : L.Get("LocalShellLogin", named),
            _ when named is not null => named,
            "pwsh" => "PowerShell 7",
            "powershell" => "Windows PowerShell",
            "wsl" => "WSL",
            "gitbash" => "Git Bash",
            _ when id.StartsWith("wsl:", StringComparison.Ordinal) => $"WSL: {Format.Visible(id[4..])}",
            _ => Format.Visible(id.Length > 0 && id[0] == '/' ? id[(id.LastIndexOf('/') + 1)..] : id),
        };
    }

    /// <summary>
    /// Welche Shell gilt, wenn der Benutzer keine gewählt hat: PowerShell 7 vor Windows PowerShell, unter Unix die
    /// Anmelde-Shell, sonst die erste, die der Kern fand. <c>null</c>, wenn es keine gibt.
    /// </summary>
    public static LocalShell? Preferred(IReadOnlyList<LocalShell> shells)
        => shells.FirstOrDefault(shell => shell.Id == "pwsh")
            ?? shells.FirstOrDefault(shell => shell.Id == "powershell")
            ?? shells.FirstOrDefault(shell => shell.Id == "login")
            ?? shells.FirstOrDefault();

    /// <summary>
    /// Der Startordner eines lokalen Terminals, wie der Kern ihn annimmt: absolut, höchstens 1024 Bytes, ohne
    /// Steuer- und Richtungszeichen. Ob es ihn gibt, sieht erst der Start nach.
    /// </summary>
    public static bool IsFolder(string? folder)
        => !string.IsNullOrEmpty(folder)
            && System.Text.Encoding.UTF8.GetByteCount(folder) <= 1024
            && Path.IsPathFullyQualified(folder)
            && !folder.Any(c => char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩');
}
