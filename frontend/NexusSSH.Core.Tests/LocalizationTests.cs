using System.Text.RegularExpressions;
using NexusSSH.Core.Localization;

namespace NexusSSH.Core.Tests;

/// <summary>
/// Hält die beiden Wörterbücher in <see cref="L"/> und ihre Verwendung zusammen. Die Quellen von App und Core
/// liegen dafür als Kopie neben den Tests (siehe csproj).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed partial class LocalizationTests
{
    private static readonly string Sources = Path.Combine(AppContext.BaseDirectory, "Sources");

    [GeneratedRegex(@"\{loc:Loc\s+Key=(\w+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex("""\bL\.Get\(\s*([^,)]+)""")]
    private static partial Regex CodeCall();

    [GeneratedRegex("""^(?:[\w.]+\s*\?\s*)?"(\w+)"(?:\s*:\s*"(\w+)")?$""")]
    private static partial Regex LiteralArgument();

    private static IEnumerable<(string File, string Key)> UsedKeys()
    {
        foreach (var file in Directory.EnumerateFiles(Sources, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in XamlKey().Matches(File.ReadAllText(file)))
            {
                yield return (Path.GetFileName(file), match.Groups[1].Value);
            }
        }

        foreach (var file in Directory.EnumerateFiles(Sources, "*.cs", SearchOption.AllDirectories))
        {
            // L.cs erklärt L.Get in Kommentaren, Loc.cs reicht den Schlüssel aus dem XAML durch.
            if (Path.GetFileName(file) is "L.cs" or "Loc.cs")
            {
                continue;
            }

            foreach (Match call in CodeCall().Matches(File.ReadAllText(file)))
            {
                var argument = LiteralArgument().Match(call.Groups[1].Value.Trim());
                Assert.True(
                    argument.Success,
                    $"{Path.GetFileName(file)}: L.Get({call.Groups[1].Value.Trim()}…) – der Schlüssel muss ein Literal sein, sonst kann dieser Test ihn nicht prüfen.");
                yield return (Path.GetFileName(file), argument.Groups[1].Value);
                if (argument.Groups[2].Success)
                {
                    yield return (Path.GetFileName(file), argument.Groups[2].Value);
                }
            }
        }
    }

    [Fact]
    public void The_sources_are_available_to_the_tests()
    {
        Assert.NotEmpty(Directory.EnumerateFiles(Sources, "MainWindow.xaml", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(Sources, "ShellViewModel.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public void Every_language_has_the_same_keys()
    {
        foreach (var language in L.Languages)
        {
            var texts = L.Texts(language);
            Assert.True(
                L.German.Keys.Order().SequenceEqual(texts.Keys.Order()),
                $"{language}: fehlt {string.Join(", ", L.German.Keys.Except(texts.Keys).Take(10))} – zu viel {string.Join(", ", texts.Keys.Except(L.German.Keys).Take(10))}");
        }
    }

    [Fact]
    public void Every_used_key_exists()
    {
        var missing = UsedKeys()
            .Where(use => !L.German.ContainsKey(use.Key))
            .Select(use => $"{use.File}: {use.Key}")
            .Distinct();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_is_used()
    {
        var used = UsedKeys().Select(use => use.Key).ToHashSet();

        var unused = L.German.Keys.Except(used).ToList();

        Assert.Empty(unused);
    }

    [Fact]
    public void Placeholders_match_between_the_languages()
    {
        foreach (var language in L.Languages)
        {
            var texts = L.Texts(language);
            foreach (var (key, german) in L.German)
            {
                Assert.True(Placeholders(german).SequenceEqual(Placeholders(texts[key])), $"{language}, {key}: {texts[key]}");
            }
        }

        static IEnumerable<string> Placeholders(string text)
            => Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order();
    }

    [Fact]
    public void No_text_is_empty()
    {
        foreach (var language in L.Languages)
        {
            Assert.DoesNotContain(L.Texts(language), pair => string.IsNullOrWhiteSpace(pair.Value));
        }
    }

    [Fact]
    public void A_text_formats_with_its_arguments_in_every_language()
    {
        // Eine geschweifte Klammer zu viel in einer Übersetzung fiele sonst erst dem Benutzer auf – als Ausnahme.
        object?[] arguments = ["a", "b", "c", "d", "e", "f"];
        foreach (var language in L.Languages)
        {
            foreach (var (key, text) in L.Texts(language))
            {
                // Manche Texte nennen selbst Platzhalter zum Eintippen ({name}) und werden nie formatiert – Maß ist
                // der deutsche Text: Lässt er sich formatieren, muss es auch die Übersetzung.
                if (Record.Exception(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, L.German[key], arguments)) is not null)
                {
                    continue;
                }

                var problem = Record.Exception(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, text, arguments));
                Assert.True(problem is null, $"{language}, {key}: {text}");
            }
        }
    }

    [Fact]
    public void The_language_of_windows_decides_when_nothing_is_chosen()
    {
        Assert.Equal(AppLanguage.German, L.FromCulture("de"));
        Assert.Equal(AppLanguage.French, L.FromCulture("FR"));
        Assert.Equal(AppLanguage.Spanish, L.FromCulture("es"));
        Assert.Equal(AppLanguage.English, L.FromCulture("en"));
        Assert.Equal(AppLanguage.English, L.FromCulture("ja"));
    }

    [Fact]
    public void Each_language_names_itself_and_its_keys()
    {
        try
        {
            L.Init(AppLanguage.French);
            Assert.Equal("Paramètres", L.Get("Settings"));
            Assert.Equal("Ctrl+Maj+P", new NexusSSH.Core.ViewModels.KeyChord("KeyP", Shift: true).Display);
            Assert.Equal("fr-FR", L.Culture.Name);

            L.Init(AppLanguage.Spanish);
            Assert.Equal("Configuración", L.Get("Settings"));
            Assert.Equal("Ctrl+Mayús+P", new NexusSSH.Core.ViewModels.KeyChord("KeyP", Shift: true).Display);
            Assert.Equal("es-ES", L.Culture.Name);
        }
        finally
        {
            L.Init(AppLanguage.English);
        }
    }

    [Fact]
    public void Get_follows_the_chosen_language_and_falls_back_to_the_key()
    {
        L.Init(AppLanguage.German);
        Assert.Equal("Einstellungen", L.Get("Settings"));
        Assert.Equal("32 Tage", L.Get("UptimeDays", 32));

        L.Init(AppLanguage.English);
        Assert.Equal("Settings", L.Get("Settings"));
        Assert.Equal("NoSuchKey", L.Get("NoSuchKey"));
    }
}
