using Attendance.Admin.Security;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Covers the one-time password an employee is issued at creation.
/// </summary>
/// <remarks>
/// The properties asserted here are the ones a person would not notice going
/// wrong. A generator that silently repeated itself, or quietly drew from a
/// smaller alphabet than intended, would still produce plausible-looking
/// passwords.
/// </remarks>
public sealed class InitialPasswordTests
{
    [Fact]
    public void GeneratesTheStatedNumberOfCharacters()
    {
        string password = InitialPassword.Generate();

        // The separators are readability only; the entropy is in the characters.
        int characters = password.Count(c => c != '-');

        Assert.Equal(InitialPassword.Length, characters);
    }

    [Fact]
    public void ExcludesCharactersThatLookAlikeOnPaper()
    {
        // An employee mistyping a password they were handed on paper is the
        // failure this control causes most often, so the ambiguous characters
        // must never appear — not merely appear rarely.
        //
        // 8 is absent from this list on purpose: it is only confusable with B,
        // and B is the one that was removed from the alphabet.
        const string ambiguous = "0O1lI5S2ZB";

        for (int attempt = 0; attempt < 500; attempt++)
        {
            string password = InitialPassword.Generate();

            Assert.DoesNotContain(password, c => ambiguous.Contains(c, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DoesNotRepeatItself()
    {
        // A generator seeded from the clock, or reusing one instance badly, would
        // pass every other test here and hand two employees the same password.
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int attempt = 0; attempt < 1_000; attempt++)
        {
            Assert.True(seen.Add(InitialPassword.Generate()), "A password was issued twice.");
        }
    }

    [Fact]
    public void DrawsOnTheWholeAlphabet()
    {
        // Guards against the generator quietly using a narrower range than
        // intended — a modulo-bias bug, for instance, can leave the tail of an
        // alphabet unreachable while everything still looks random.
        HashSet<char> observed = [];

        for (int attempt = 0; attempt < 2_000; attempt++)
        {
            foreach (char c in InitialPassword.Generate().Where(c => c != '-'))
            {
                observed.Add(c);
            }
        }

        Assert.Equal(50, observed.Count);
    }

    [Fact]
    public void IsGroupedForReadingAloud()
    {
        string password = InitialPassword.Generate();

        Assert.Equal(
            string.Join('-', Enumerable.Range(0, 4).Select(_ => "?????")).Length,
            password.Length);

        Assert.All(
            password.Split('-'),
            group => Assert.Equal(5, group.Length));
    }

    [Fact]
    public void ContainsNothingThatWouldBreakACsvExportOrAShellPaste()
    {
        // Issued passwords get pasted into terminals and typed from printouts.
        // Quotes, commas and backslashes cause a class of support call that has
        // nothing to do with the password being wrong.
        const string troublesome = "\"',;\\/ \t";

        for (int attempt = 0; attempt < 200; attempt++)
        {
            string password = InitialPassword.Generate();

            Assert.DoesNotContain(password, c => troublesome.Contains(c, StringComparison.Ordinal));
        }

        // Deliberately NOT asserted: that every password contains both cases.
        // A uniform draw occasionally produces one that does not, so such a test
        // would fail roughly once in three thousand runs — and a test that fails
        // rarely for a correct reason is worse than no test, because it teaches
        // people to re-run the suite.
    }
}
