using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Selbst-Update: das richtige Setup wählen, laden, gegen GitHubs Angaben prüfen, starten – nur auf Zustimmung.</summary>
[Collection(LanguageCollection.Name)]
public sealed class SelfUpdateTests : IDisposable
{
    private const string Repo = "Ariazonaa/NexusSSH";
    private static readonly byte[] Setup = [.. Enumerable.Range(0, 300_000).Select(index => (byte)(index * 7))];
    private static readonly string SetupSha = Convert.ToHexStringLower(SHA256.HashData(Setup));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nexus-update-{Guid.NewGuid():N}");

    public SelfUpdateTests() => L.Init(AppLanguage.English);

    public void Dispose() => UpdateDownloader.RemoveStale(_directory);

    private static UpdateAsset Asset(string name = "NexusSSH-Setup-0.7.0.exe", long? size = null, string? sha = null, string tag = "v0.7.0")
        => new(name, size ?? Setup.Length, sha ?? SetupSha, new Uri($"https://github.com/{Repo}/releases/download/{tag}/{name}"));

    private static UpdateInfo Update(params UpdateAsset[] assets)
        => new("0.7.0", "NexusSSH 0.7.0", $"https://github.com/{Repo}/releases/tag/v0.7.0") { Tag = "v0.7.0", Assets = assets };

    [Fact]
    public void The_release_answer_names_the_files_with_size_and_sha256()
    {
        var json = $$"""
            {"tag_name":"v0.7.0","name":"NexusSSH 0.7.0","html_url":"https://github.com/{{Repo}}/releases/tag/v0.7.0","assets":[
              {"name":"NexusSSH-Setup-0.7.0.exe","size":31100102,"digest":"sha256:{{SetupSha}}","browser_download_url":"https://github.com/{{Repo}}/releases/download/v0.7.0/NexusSSH-Setup-0.7.0.exe"},
              {"name":"no-digest.exe","size":1,"browser_download_url":"https://github.com/x/y"},
              {"name":"upper.exe","size":1,"digest":"sha256:{{SetupSha.ToUpperInvariant()}}","browser_download_url":"https://github.com/x/y"},
              {"name":"md5.exe","size":1,"digest":"md5:abc","browser_download_url":"https://github.com/x/y"},
              null]}
            """;

        var update = UpdateCheck.Parse(json, new Version(0, 6, 1))!;

        Assert.Equal("v0.7.0", update.Tag);
        var asset = Assert.Single(update.Assets);
        Assert.Equal(("NexusSSH-Setup-0.7.0.exe", 31100102L, SetupSha), (asset.Name, asset.Size, asset.Sha256));
    }

    [Fact]
    public void Only_the_setup_of_this_architecture_from_this_repository_and_tag_is_chosen()
    {
        var x64 = Asset();
        var arm = Asset("NexusSSH-Setup-0.7.0-arm64.exe");
        var update = Update(arm, x64);

        Assert.Same(x64, UpdateSetup.Choose(update, Repo, Architecture.X64));
        Assert.Same(arm, UpdateSetup.Choose(update, Repo, Architecture.Arm64));
        Assert.Null(UpdateSetup.Choose(update, Repo, Architecture.X86));
        Assert.Null(UpdateSetup.Choose(update, "someone/else", Architecture.X64));
        Assert.Null(UpdateSetup.Choose(update with { Tag = string.Empty }, Repo, Architecture.X64));
        Assert.Null(UpdateSetup.Choose(Update(Asset(tag: "v0.6.0")), Repo, Architecture.X64));
        Assert.Null(UpdateSetup.Choose(Update(Asset(size: UpdateSetup.MaxSize + 1)), Repo, Architecture.X64));
        Assert.Null(UpdateSetup.Choose(Update(x64 with { Url = new Uri($"http://github.com/{Repo}/releases/download/v0.7.0/NexusSSH-Setup-0.7.0.exe") }), Repo, Architecture.X64));
        Assert.Null(UpdateSetup.Choose(Update(x64 with { Url = new Uri("https://evil.example/NexusSSH-Setup-0.7.0.exe") }), Repo, Architecture.X64));
    }

    [Fact]
    public async Task A_matching_setup_is_downloaded_over_githubs_redirect_and_checked()
    {
        var handler = new Server();
        var progress = new List<double>();

        var path = await new UpdateDownloader(new HttpClient(handler)).DownloadAsync(Asset(), _directory, progress.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Setup, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(_directory, "NexusSSH-Setup-0.7.0.exe"), path);
        Assert.Equal(["github.com", "release-assets.githubusercontent.com"], handler.Hosts);
        Assert.Equal(1.0, progress[^1]);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("size")]
    [InlineData("longer")]
    [InlineData("bad_source")]
    [InlineData("http")]
    public async Task What_does_not_match_is_refused_and_nothing_stays_behind(string fault)
    {
        var handler = new Server
        {
            Body = fault switch
            {
                "checksum" => [.. Setup.Select(value => (byte)(value ^ 1))],
                "size" => Setup[..^10],
                "longer" => [.. Setup, 0],
                _ => Setup,
            },
            Redirect = fault == "bad_source" ? "https://evil.example/file" : "https://release-assets.githubusercontent.com/file",
            Status = fault == "http" ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            WithLength = false,
        };

        var error = await Assert.ThrowsAsync<UpdateException>(
            () => new UpdateDownloader(new HttpClient(handler)).DownloadAsync(Asset(), _directory, null, TestContext.Current.CancellationToken));

        Assert.Equal(fault is "longer" ? "size" : fault, error.Code);
        Assert.Empty(Directory.Exists(_directory) ? Directory.GetFiles(_directory) : []);
    }

    [Fact]
    public async Task Yes_downloads_checks_starts_the_setup_and_ends_the_app()
    {
        var (shell, prompts, installer) = Shell(new Server());
        var ended = false;
        shell.ExitForUpdate += () => ended = true;
        Assert.True(shell.CanInstallUpdate);

        prompts.Confirm = true;
        await shell.InstallUpdateAsync();

        Assert.Single(prompts.Confirmed);
        Assert.Equal(Path.Combine(_directory, "NexusSSH-Setup-0.7.0.exe"), installer.Started);
        Assert.True(ended);
        Assert.False(shell.IsUpdating);
    }

    [Fact]
    public async Task No_does_nothing_and_a_bad_file_is_only_reported()
    {
        var (shell, prompts, installer) = Shell(new Server { Body = [.. Setup.Reverse()] });
        var ended = false;
        shell.ExitForUpdate += () => ended = true;

        prompts.Confirm = false;
        await shell.InstallUpdateAsync();
        Assert.Null(installer.Started);
        Assert.False(Directory.Exists(_directory));

        prompts.Confirm = true;
        await shell.InstallUpdateAsync();
        Assert.Null(installer.Started);
        Assert.False(ended);
        Assert.Contains(L.Get("UpdateFailedChecksum"), shell.UpdateProblem, StringComparison.Ordinal);
        Assert.Contains(shell.Problems, entry => entry.Text.Contains("checksum", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_installation_there_is_only_the_link()
    {
        var (shell, _, installer) = Shell(new Server());
        installer.CanInstall = false;
        shell.ConfigureUpdates(installer, new UpdateDownloader(new HttpClient(new Server())), Repo);

        Assert.True(shell.HasUpdate);
        Assert.False(shell.CanInstallUpdate);
    }

    private (ShellViewModel Shell, FakePrompts Prompts, FakeInstaller Installer) Shell(Server server)
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[]}""";
        var prompts = new FakePrompts();
        var shell = new ShellViewModel(new CoreBackend(transport), new ImmediateDispatcher(), new AppSettings(), prompts, updates: new Found(Update(Asset(), Asset("NexusSSH-Setup-0.7.0-arm64.exe"))));
        shell.CheckForUpdatesAsync(manual: true).GetAwaiter().GetResult();
        var installer = new FakeInstaller(_directory);
        shell.ConfigureUpdates(installer, new UpdateDownloader(new HttpClient(server)), Repo);
        return (shell, prompts, installer);
    }

    private sealed class Found(UpdateInfo update) : IUpdateService
    {
        public Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default) => Task.FromResult<UpdateInfo?>(update);
    }

    private sealed class FakeInstaller(string directory) : IUpdateInstaller
    {
        public bool CanInstall { get; set; } = true;

        public string DownloadDirectory => directory;

        public string? Started { get; private set; }

        public void Start(string setupPath) => Started = setupPath;
    }

    // GitHub im Kleinen: github.com leitet auf den Server für Dateien weiter, der liefert die Bytes.
    private sealed class Server : HttpMessageHandler
    {
        public byte[] Body { get; init; } = Setup;

        public string Redirect { get; init; } = "https://release-assets.githubusercontent.com/github-production-release-asset/1?sig=x";

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public bool WithLength { get; init; } = true;

        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri(Redirect);
                return Task.FromResult(redirect);
            }

            var content = new StreamContent(new MemoryStream(Body));
            if (!WithLength)
            {
                content.Headers.ContentLength = null;
            }

            return Task.FromResult(new HttpResponseMessage(Status) { Content = content, RequestMessage = request });
        }
    }
}
