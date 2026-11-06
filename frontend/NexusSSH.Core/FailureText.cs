using NexusSSH.Core.Localization;

namespace NexusSSH.Core;

/// <summary>
/// Fehler des Kerns bei Dateiaktionen und Transfers als Satz: die Art übersetzt, die englische Meldung des Servers
/// als Einzelheit in Klammern dahinter.
/// </summary>
public static class FailureText
{
    /// <summary>Satz zu einer Fehlerart (<c>permission_denied</c>, <c>not_found</c> …) samt Meldung.</summary>
    public static string Describe(string? code, string? message)
    {
        var text = code switch
        {
            "sudo_password" => L.Get("FailureSudoPassword"),
            "sudo_refused" => L.Get("FailureSudoRefused"),
            "no_sudo" or "no_sftp_server" => L.Get("FailureNoSudo"),
            "not_found" => L.Get("SftpNotFound"),
            "permission_denied" => L.Get("SftpPermissionDenied"),
            "exists" => L.Get("SftpExists"),
            "not_a_file" => L.Get("SftpNotAFile"),
            "connection" or "no_sftp" => L.Get("SftpConnection"),
            "timeout" => L.Get("SftpTimeout"),
            "too_many" => L.Get("SftpTooMany"),
            "no_exec" => L.Get("ExecNotAllowed"),
            "no_channel" => L.Get("ExecNoChannel"),
            "command_failed" => L.Get("ExecFailed"),
            "unknown_owner" => L.Get("SftpUnknownOwner"),
            "not_connected" => L.Get("TransferNotConnected"),
            "interrupted" => L.Get("TransferInterrupted"),
            "paused" => L.Get("TransferPausedHint"),
            "unsafe_name" => L.Get("TransferUnsafeName"),
            "local_not_found" => L.Get("TransferLocalNotFound"),
            "local_permission_denied" => L.Get("TransferLocalDenied"),
            "local_io" => L.Get("TransferLocalFailed"),
            _ => L.Get("SftpFailed"),
        };

        // Sagt die Meldung nur dasselbe noch einmal („Permission denied“), bleibt sie weg.
        var detail = message?.Trim() ?? string.Empty;
        return detail.Length == 0 || string.Equals(detail.TrimEnd('.'), text.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} ({Format.Visible(detail)})";
    }

    /// <summary>
    /// Ein Transfer meldet Art und Meldung in einem Text (<c>permission_denied: Permission denied</c>). Steht keine
    /// Art davor, bleibt der Text, wie er ist.
    /// </summary>
    public static string OfTransfer(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var colon = detail.IndexOf(':');
        return colon > 0 && detail.AsSpan(0, colon).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz_") < 0
            ? Describe(detail[..colon], detail[(colon + 1)..])
            : Format.Visible(detail);
    }
}
