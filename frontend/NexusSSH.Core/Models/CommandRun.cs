namespace NexusSSH.Core.Models;

/// <summary>Was ein Befehl hinterlassen hat, der über das Werkzeug <c>run_command</c> lief. Der Text ist roh vom Server.</summary>
/// <param name="Status">Rückgabewert; <c>null</c>, wenn der Server keinen nannte oder der Befehl noch lief.</param>
/// <param name="Output">Standardausgabe.</param>
/// <param name="Errors">Fehlerausgabe.</param>
/// <param name="Cut">Einer der Ströme war länger als die Grenze des Kerns.</param>
/// <param name="TimedOut">Die Frist lief ab; der Befehl kann am Server weiterlaufen.</param>
public sealed record CommandRun(int? Status, string Output, string Errors, bool Cut, bool TimedOut);
