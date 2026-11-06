using System.Net;
using NexusSSH.Core.Backend;
using NexusSSH.Core.Localization;
using NexusSSH.Core.Services;
using NexusSSH.Core.Settings;
using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Update-Prüfung: aus der GitHub-Antwort ein Update machen, und der Weg bis in die Oberfläche.</summary>
[Collection(LanguageCollection.Name)]
public sealed class UpdateCheckTests
{
    private static readonly Version Current = new(0, 20, 1);

    private static string Release(string tag, bool draft = false, bool prerelease = false)
        => $$"""{"tag_name":"{{tag}}","name":"NexusSSH {{tag}}","html_url":"https://github.com/Ariazonaa/NexusSSH/releases/tag/{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}}}""";

    [Fact]
    public void A_newer_release_is_reported_with_version_name_and_url()
    {
        var update = UpdateCheck.Parse(Release("v0.21.0"), Current);

        Assert.NotNull(update);
        Assert.Equal("0.21.0", update!.Version);
        Assert.Equal("NexusSSH v0.21.0", update.Name);
        Assert.Equal("https://github.com/Ariazonaa/NexusSSH/releases/tag/v0.21.0", update.Url);
    }

    [Theory]
    [InlineData("v0.20.1")] // gleich
    [InlineData("v0.20.0")] // älter
    [InlineData("0.19.9")] // älter, ohne v
    public void The_same_or_an_older_release_is_no_update(string tag)
        => Assert.Null(UpdateCheck.Parse(Release(tag), Current));

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        Assert.Null(UpdateCheck.Parse(Release("v0.21.0", draft: true), Current));
        Assert.Null(UpdateCheck.Parse(Release("v0.21.0", prerelease: true), Current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"","draft":false,"prerelease":false}""")]
    [InlineData("""{"tag_name":"latest","draft":false,"prerelease":false}""")]
    public void Unreadable_or_versionless_answers_are_no_update(string json)
        => Assert.Null(UpdateCheck.Parse(json, Current));

    [Theory]
    [InlineData("v0.21.0", "0.21.0")]
    [InlineData("0.21", "0.21.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v0.21.0-rc1", "0.21.0")]
    [InlineData("0.22.0+build.5", "0.22.0")]
    public void A_version_is_read_from_the_tag(string tag, string expected)
        => Assert.Equal(expected, UpdateCheck.ParseVersion(tag)?.ToString(3));

    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    public void A_tag_without_numbers_has_no_version(string tag)
        => Assert.Null(UpdateCheck.ParseVersion(tag));

    [Fact]
    public async Task The_github_service_asks_the_right_url_with_a_user_agent()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release("v0.21.0")) });
        var service = new GitHubUpdateService(new HttpClient(handler), "Ariazonaa/NexusSSH");

        var update = await service.CheckAsync(Current, TestContext.Current.CancellationToken);

        Assert.Equal("0.21.0", update!.Version);
        Assert.Equal("https://api.github.com/repos/Ariazonaa/NexusSSH/releases/latest", handler.Request!.RequestUri!.ToString());
        Assert.Contains(handler.Request.Headers.UserAgent, product => product.Product?.Name == "NexusSSH-UpdateCheck");
    }

    [Fact]
    public async Task A_non_success_status_is_no_update()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new GitHubUpdateService(new HttpClient(handler), "Ariazonaa/NexusSSH");

        Assert.Null(await service.CheckAsync(Current, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_network_error_is_swallowed_as_no_update()
    {
        var service = new GitHubUpdateService(new HttpClient(new ThrowingHandler()), "Ariazonaa/NexusSSH");

        Assert.Null(await service.CheckAsync(Current, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_manual_check_shows_the_banner_when_newer()
    {
        var update = new UpdateInfo("0.21.0", "NexusSSH 0.21.0", "https://example/release");
        using var shell = Shell(new FakeUpdates(update));

        await shell.CheckForUpdatesAsync(manual: true);

        Assert.Same(update, shell.AvailableUpdate);
        Assert.True(shell.HasUpdate);

        shell.DismissUpdate();
        Assert.Null(shell.AvailableUpdate);
        Assert.False(shell.HasUpdate);
    }

    [Fact]
    public async Task A_manual_check_reports_when_up_to_date()
    {
        using var shell = Shell(new FakeUpdates(null));

        await shell.CheckForUpdatesAsync(manual: true);

        Assert.Null(shell.AvailableUpdate);
        Assert.NotEmpty(shell.Output);
    }

    [Fact]
    public async Task The_startup_check_only_runs_once_a_day()
    {
        var fake = new FakeUpdates(null);
        var settings = new AppSettings { CheckForUpdates = true, LastUpdateCheck = DateTimeOffset.UtcNow.ToString("o") };
        using var shell = Shell(fake, settings);

        await shell.CheckForUpdatesAsync(manual: false);

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task The_startup_check_is_skipped_when_turned_off()
    {
        var fake = new FakeUpdates(new UpdateInfo("9.9.9", "x", "y"));
        using var shell = Shell(fake, new AppSettings { CheckForUpdates = false });

        await shell.CheckForUpdatesAsync(manual: false);

        Assert.Equal(0, fake.Calls);
        Assert.Null(shell.AvailableUpdate);
    }

    [Fact]
    public async Task The_startup_check_runs_when_enabled_and_never_checked()
    {
        var update = new UpdateInfo("0.21.0", "NexusSSH 0.21.0", "https://example/release");
        var settings = new AppSettings { CheckForUpdates = true, LastUpdateCheck = string.Empty };
        using var shell = Shell(new FakeUpdates(update), settings);

        await shell.CheckForUpdatesAsync(manual: false);

        Assert.Same(update, shell.AvailableUpdate);
        Assert.False(string.IsNullOrEmpty(settings.LastUpdateCheck));
    }

    private static ShellViewModel Shell(IUpdateService updates, AppSettings? settings = null)
    {
        var transport = new FakeTransport();
        transport.Responses["profiles.list"] = """{"ok":true,"profiles":[]}""";
        return new ShellViewModel(new CoreBackend(transport), new ImmediateDispatcher(), settings ?? new AppSettings(), new FakePrompts(), updates: updates);
    }

    private sealed class FakeUpdates(UpdateInfo? result) : IUpdateService
    {
        public int Calls { get; private set; }

        public Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
