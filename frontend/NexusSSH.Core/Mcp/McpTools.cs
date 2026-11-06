namespace NexusSSH.Core.Mcp;

/// <summary>Ein Aufruf eines Werkzeugs: sein Name und die Argumente als JSON-Objekt, wie sie kamen – fremde Eingabe.</summary>
public sealed record McpCall(string Tool, string Arguments);

/// <summary>Was ein Werkzeug antwortet. Der Text geht an die KI; Texte des Servers darin sind vorher gesäubert.</summary>
public sealed record McpResult(string Text, bool IsError = false);

/// <summary>Ein Werkzeug, wie das MCP-Programm es sieht. <paramref name="Schema"/> ist JSON Schema für die Argumente.</summary>
public sealed record McpTool(string Name, string Description, string Schema, bool IsReadOnly = true);

/// <summary>
/// Die Werkzeuge des MCP-Servers. Die Texte sind für die KI und deshalb englisch; was ein Werkzeug tut, steht in
/// <c>ShellViewModel.Mcp.cs</c>. Ein neues Werkzeug ist ein Eintrag hier und einer dort.
/// </summary>
public static class McpTools
{
    public const string Servers = "servers";
    public const string AppLog = "app_log";
    public const string Connect = "connect";
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string ServerLogs = "server_logs";
    public const string ServerStats = "server_stats";
    public const string RunCommand = "run_command";

    /// <summary>So viel einer Datei liefert <see cref="ReadFile"/> je Aufruf höchstens.</summary>
    public const int MaxReadLength = 48 * 1024;

    /// <summary>So lang darf ein Befehl sein, wie im Kern (<c>exec::RUN_COMMAND</c>).</summary>
    public const int MaxCommandLength = 16 * 1024;

    /// <summary>So lange darf ein Befehl höchstens laufen, wie im Kern (<c>exec::LIMIT</c>).</summary>
    public const int MaxCommandSeconds = 300;

    /// <summary>So lange läuft ein Befehl, wenn der Aufruf nichts anderes sagt.</summary>
    public const int DefaultCommandSeconds = 60;

    private const string ServerArgument = """
        "server":{"type":"string","description":"Name or id of a server from the servers tool."}
        """;

    private static string Schema(string properties, params string[] required)
        => "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + string.Join(',', required.Select(name => $"\"{name}\"")) + "]}";

    private const string NoArguments = """{"type":"object","properties":{}}""";

    public static IReadOnlyList<McpTool> All { get; } =
    [
        new(
            Servers,
            "Lists the servers the user released for this tool, with their state. A server missing here is out of reach.",
            NoArguments),
        new(
            AppLog,
            "Reads the Output and Problems panels of NexusSSH: connections, failures, warnings. Servers that are not released are masked.",
            """{"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":200,"default":100,"description":"Newest entries of each panel."}}}"""),
        new(
            Connect,
            "Connects to a released server in a background tab of NexusSSH, or reuses a connection that stands. If a password or host key is needed, the user is asked in the app.",
            Schema(ServerArgument, "server"),
            IsReadOnly: false),
        new(
            ListFiles,
            "Lists a directory on a connected server.",
            Schema(ServerArgument + ""","path":{"type":"string","description":"Absolute path as the server's file service names it."}""", "server","path")),
        new(
            ReadFile,
            "Reads part of a text file on a connected server. Binary content is reported, not returned.",
            Schema(ServerArgument + ""","path":{"type":"string","description":"Absolute path."},"offset":{"type":"integer","minimum":0,"default":0},"length":{"type":"integer","minimum":1,"maximum":49152,"default":49152}""", "server","path")),
        new(
            ServerLogs,
            "Reads the systemd journal of a connected server, or the end of a log file when path is given.",
            Schema(ServerArgument + ""","path":{"type":"string","description":"Log file; without it the journal is read."},"unit":{"type":"string","description":"Journal only: name of a systemd unit."},"since":{"type":"string","enum":["15m","1h","6h","1d","7d"],"description":"Journal only."},"priority":{"type":"integer","minimum":0,"maximum":7,"description":"Journal only: this severity and worse."},"lines":{"type":"integer","minimum":1,"maximum":2000,"default":200}""", "server")),
        new(
            ServerStats,
            "Reads CPU, memory, disk, load and uptime of a connected server.",
            Schema(ServerArgument, "server")),
        new(
            RunCommand,
            "Runs one shell command on a connected server. The user must approve the exact text in NexusSSH every time; it never runs otherwise. Where possible it is typed into the user's terminal, where they watch it and where cd and variables persist: a half-typed line there is cleared, stdout and stderr arrive together in stdout, and a command that outlives timeout_seconds keeps running. It is refused while a full-screen program runs there, and if the shell does not show the approved line, Enter is not pressed and nothing runs. Pagers can hang, so prefer --no-pager or a pipe to cat. Otherwise (the result says terminal: false) it runs in its own channel with separate streams. If sudo asks for a password, the user types it; it never reaches you. Long output keeps its start and end, the middle is left out and the result says cut: true; for all of it, write to a file and use read_file with offset.",
            Schema(ServerArgument + ""","command":{"type":"string","description":"The command as sh (or PowerShell on a Windows server) reads it."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":300,"default":60}""", "server","command"),
            IsReadOnly: false),
    ];
}
