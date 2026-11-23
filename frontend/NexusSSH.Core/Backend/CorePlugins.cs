using NexusSSH.Core.Services;

namespace NexusSSH.Core.Backend;

/// <summary>Plugins über den Kern: <c>plugin.inspect</c> antwortet gleich, <c>plugin.call</c> als <c>sftp.result</c>.</summary>
internal sealed class CorePlugins(CoreBackend backend) : IPluginRuntime
{
    public PluginInspection Inspect(string path)
    {
        var response = backend.Send(new RequestDto("plugin.inspect") { Path = path });
        return response is { Ok: true, Manifest: { } manifest, Sha256: { } sha256 }
            ? new PluginInspection(manifest, sha256)
            : throw new PluginException(response.Error ?? "plugin_invalid", response.Message ?? string.Empty);
    }

    public async Task<string> CallAsync(string path, string sha256, string input, CancellationToken cancellation = default)
    {
        try
        {
            var result = await backend.SftpAsync(new RequestDto("plugin.call") { Path = path, Sha256 = sha256, Input = input }, cancellation);
            return result.Output ?? throw new PluginException("plugin_output", "no answer");
        }
        catch (RemoteFileException e)
        {
            throw new PluginException(e.Code, e.Message);
        }
    }
}
