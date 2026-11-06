namespace NexusSSH.Core.Tests;

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/var/www/", "/var/www")]
    [InlineData("//var///www", "/var/www")]
    [InlineData("/var/./www", "/var/www")]
    [InlineData("/var/www/../log", "/var/log")]
    [InlineData("/../..", "/")]
    public void Normalize_yields_an_absolute_path_without_noise(string input, string expected)
        => Assert.Equal(expected, RemotePath.Normalize(input));

    [Theory]
    [InlineData("/", null)]
    [InlineData("/var", "/")]
    [InlineData("/var/www", "/var")]
    public void Parent_stops_at_the_root(string path, string? expected)
        => Assert.Equal(expected, RemotePath.Parent(path));

    [Fact]
    public void Combine_joins_directory_and_name()
    {
        Assert.Equal("/var/www/html", RemotePath.Combine("/var/www", "html"));
        Assert.Equal("/etc", RemotePath.Combine("/", "etc"));
    }

    [Theory]
    [InlineData("/var/www/html", "html")]
    [InlineData("/var/www/", "www")]
    [InlineData("/", "")]
    public void Name_is_the_last_part(string path, string expected)
        => Assert.Equal(expected, RemotePath.Name(path));

    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData(".bashrc", true)]
    [InlineData("with space", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("new\nline", false)]
    public void Only_a_single_ordinary_path_part_is_a_valid_name(string name, bool expected)
        => Assert.Equal(expected, RemotePath.IsValidName(name));

    [Fact]
    public void Segments_start_at_the_root_and_end_at_the_path()
    {
        var segments = RemotePath.Segments("/var/www");

        Assert.Equal(["/", "var", "www"], segments.Select(segment => segment.Name));
        Assert.Equal(["/", "/var", "/var/www"], segments.Select(segment => segment.FullPath));
    }
}
