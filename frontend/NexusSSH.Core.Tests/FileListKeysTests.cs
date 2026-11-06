using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

public sealed class FileListKeysTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("F5", false, false, false, true, FileListAction.Refresh)]
    [InlineData("F5", false, false, false, false, FileListAction.Refresh)]
    [InlineData("Enter", false, false, false, true, FileListAction.Open)]
    [InlineData("Enter", false, false, true, true, FileListAction.Properties)]
    [InlineData("Enter", false, false, true, false, FileListAction.None)] // Eigenschaften nur am Server
    [InlineData("F2", false, false, false, false, FileListAction.Rename)]
    [InlineData("L", true, false, false, false, FileListAction.FocusPath)]
    [InlineData("D", false, false, true, true, FileListAction.FocusPath)]
    [InlineData("F4", false, false, false, true, FileListAction.FocusPath)]
    [InlineData("Delete", false, false, false, false, FileListAction.Delete)]
    [InlineData("Delete", false, true, false, false, FileListAction.DeleteForever)]
    [InlineData("Delete", false, true, false, true, FileListAction.Delete)] // am Server gibt es keinen Papierkorb
    [InlineData("Back", false, false, false, true, FileListAction.Up)]
    [InlineData("Up", false, false, true, false, FileListAction.Up)]
    [InlineData("Left", false, false, true, false, FileListAction.Back)]
    [InlineData("Right", false, false, true, true, FileListAction.Forward)]
    [InlineData("Left", false, false, false, true, FileListAction.None)]
    [InlineData("A", true, false, false, false, FileListAction.SelectAll)]
    [InlineData("F", true, false, false, true, FileListAction.FocusFilter)]
    [InlineData("F", true, false, false, false, FileListAction.None)] // links kein Filter
    public void Keys_do_the_same_in_both_lists(string key, bool control, bool shift, bool alt, bool remote, FileListAction expected)
        => Assert.Equal(expected, FileListKeys.ActionFor(key, control, shift, alt, remote));

    [Fact]
    public void Typing_jumps_to_a_name_and_the_same_letter_again_goes_round()
    {
        string[] names = ["apfel", "ast", "birne"];
        var typing = new TypeAhead(new Clock());

        Assert.Equal(0, typing.Next('a', names, -1));
        Assert.Equal(1, typing.Next('a', names, 0));
        Assert.Equal(0, typing.Next('a', names, 1));
    }

    [Fact]
    public void More_letters_narrow_the_name_down()
    {
        string[] names = ["apfel", "ast", "birne"];
        var typing = new TypeAhead(new Clock());

        Assert.Equal(0, typing.Next('a', names, -1));
        Assert.Equal(1, typing.Next('s', names, 0));
        Assert.Null(typing.Next('x', names, 1));
    }

    [Fact]
    public void A_pause_starts_the_search_anew()
    {
        string[] names = ["apfel", "ast", "birne"];
        var clock = new Clock();
        var typing = new TypeAhead(clock);

        Assert.Equal(0, typing.Next('a', names, -1));
        clock.Now += TimeSpan.FromSeconds(1.1);
        Assert.Equal(2, typing.Next('b', names, 0)); // ohne die Pause suchte es „ab“
    }

    [Fact]
    public void A_space_belongs_to_the_name_only_while_typing()
    {
        var clock = new Clock();
        var typing = new TypeAhead(clock);
        Assert.False(typing.IsTyping);

        typing.Next('a', ["a b"], -1);
        Assert.True(typing.IsTyping);
        clock.Now += TimeSpan.FromSeconds(1.1);
        Assert.False(typing.IsTyping);
    }

    [Fact]
    public void Umlauts_and_case_do_not_matter()
    {
        Assert.Equal(1, new TypeAhead(new Clock()).Next('ä', ["Zebra", "Ärger"], -1));
        Assert.Equal(1, new TypeAhead(new Clock()).Next('Z', ["apfel", "zebra"], -1));
        Assert.Equal(0, new TypeAhead(new Clock()).Next('a', ["Ärger", "Zebra"], -1));
    }
}
