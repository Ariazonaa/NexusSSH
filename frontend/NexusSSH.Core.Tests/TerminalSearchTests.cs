using NexusSSH.Core.ViewModels;

namespace NexusSSH.Core.Tests;

/// <summary>Suche im Terminal (Spec §5) und die Trefferliste im unteren Bereich (Spec §7).</summary>
[Collection(LanguageCollection.Name)]
public sealed class TerminalSearchTests
{
    private static (TerminalSearchViewModel Search, List<FindRequest> Finds) Create()
    {
        TestShell.Create(out _);
        var search = new TerminalSearchViewModel();
        var finds = new List<FindRequest>();
        search.FindRequested += finds.Add;
        return (search, finds);
    }

    [Fact]
    public void Typing_searches_incrementally_and_enter_moves_on()
    {
        var (search, finds) = Create();

        search.Query = "err";
        search.Query = "error";
        search.FindNext();
        search.FindPrevious();

        Assert.Equal(
            [
                new FindRequest("err", false, Backwards: false, Incremental: true),
                new FindRequest("error", false, Backwards: false, Incremental: true),
                new FindRequest("error", false, Backwards: false, Incremental: false),
                new FindRequest("error", false, Backwards: true, Incremental: false),
            ],
            finds);
    }

    [Fact]
    public void Changing_the_case_option_searches_again()
    {
        var (search, finds) = Create();
        search.Query = "Error";

        search.MatchCase = true;

        Assert.Equal(new FindRequest("Error", true, Backwards: false, Incremental: true), finds[^1]);
    }

    [Fact]
    public void The_case_option_also_refreshes_the_list()
    {
        var (search, _) = Create();
        var asked = new List<(string Query, bool MatchCase)>();
        search.ListRequested += (query, matchCase) => asked.Add((query, matchCase));

        search.MatchCase = true;
        Assert.Empty(asked);

        search.ListQuery = "Error";
        search.MatchCase = false;
        Assert.Equal([("Error", false)], asked);
    }

    [Fact]
    public void An_empty_query_clears_instead_of_searching()
    {
        var (search, finds) = Create();
        var cleared = 0;
        search.ClearRequested += () => cleared++;
        search.Query = "x";
        search.ReportFound(0, 3);

        search.Query = string.Empty;
        search.FindNext();

        Assert.Single(finds);
        Assert.Equal(2, cleared);
        Assert.Equal(string.Empty, search.Summary);
    }

    [Fact]
    public void The_summary_names_position_and_count()
    {
        var (search, _) = Create();
        search.Query = "error";

        search.ReportFound(2, 12);
        Assert.Equal("3 of 12", search.Summary);

        search.ReportFound(-1, 2000);
        Assert.Equal("2000 matches", search.Summary);

        search.ReportFound(-1, 0);
        Assert.Equal("No matches", search.Summary);
    }

    [Fact]
    public void Closing_the_bar_clears_the_highlights_but_keeps_the_query()
    {
        var (search, _) = Create();
        var cleared = 0;
        search.ClearRequested += () => cleared++;
        search.Open();
        search.Query = "error";
        search.ReportFound(0, 1);

        search.Close();

        Assert.False(search.IsOpen);
        Assert.Equal(1, cleared);
        Assert.Equal(string.Empty, search.Summary);
        Assert.Equal("error", search.Query);
    }

    [Fact]
    public void The_list_asks_all_terminals_and_shows_what_comes_back()
    {
        var (search, _) = Create();
        var asked = new List<(string Query, bool MatchCase)>();
        search.ListRequested += (query, matchCase) => asked.Add((query, matchCase));

        search.ListQuery = "  error ";
        search.SearchAll();
        Assert.Equal([("error", false)], asked);

        search.ReportHits("error", [new SearchHit("a", "web01", 3, "error: disk full"), new SearchHit("b", "db01", 9, "no error")], more: false);

        Assert.Equal(2, search.Hits.Count);
        Assert.Equal("2 lines in 2 session(s)", search.ListSummary);
        Assert.Equal("web01: error: disk full", search.Hits[0].ToString());
    }

    [Fact]
    public void An_answer_to_an_outdated_query_is_ignored()
    {
        var (search, _) = Create();
        search.ListQuery = "warning";
        search.SearchAll();

        search.ReportHits("error", [new SearchHit("a", "web01", 3, "error")], more: false);

        Assert.Empty(search.Hits);
    }

    [Fact]
    public void No_hits_and_too_many_hits_are_said_plainly()
    {
        var (search, _) = Create();
        search.ListQuery = "x";

        search.ReportHits("x", [], more: false);
        Assert.Equal("No matches", search.ListSummary);

        search.ReportHits("x", [new SearchHit("a", "web01", 1, "x")], more: true);
        Assert.Equal("More than 1 lines – showing the first", search.ListSummary);

        search.ListQuery = " ";
        search.SearchAll();
        Assert.Empty(search.Hits);
        Assert.Equal(string.Empty, search.ListSummary);
    }

    [Fact]
    public void Clicking_a_hit_asks_to_reveal_it_with_the_query_that_found_it()
    {
        var (search, _) = Create();
        (SearchHit Hit, string Query, bool MatchCase)? revealed = null;
        search.RevealRequested += (hit, query, matchCase) => revealed = (hit, query, matchCase);
        search.ListQuery = "error";
        search.MatchCase = true;
        var hit = new SearchHit("a", "web01", 3, "error: disk full");

        search.Reveal(hit);

        Assert.Equal((hit, "error", true), revealed);
    }
}
