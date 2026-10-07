using Attendance.Application.Services;
using Xunit;

namespace Attendance.Application.Tests;

/// <summary>
/// Tests for <see cref="AdministratorPasswordPolicy"/>.
/// </summary>
/// <remarks>
/// The policy is deliberately small — length, not composition (NIST SP 800-63B
/// §5.1.1.2) — so these tests pin both what it refuses and, just as importantly,
/// what it must not refuse. A rule that quietly started rejecting passphrases with
/// spaces would push administrators back towards short, predictable passwords.
/// </remarks>
public sealed class AdministratorPasswordPolicyTests
{
    private const string UserName = "j.okafor";
    private const string Current = "the old passphrase in use";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                    ")]
    public void RefusesABlankPassword(string? candidate) =>
        Assert.Equal(PasswordPolicyViolation.Blank, AdministratorPasswordPolicy.Check(candidate, Current, UserName));

    [Fact]
    public void RefusesElevenCharactersAndAcceptsTwelve()
    {
        Assert.Equal(PasswordPolicyViolation.TooShort, AdministratorPasswordPolicy.Check("abcdefghijk", Current, UserName));
        Assert.Equal(PasswordPolicyViolation.None, AdministratorPasswordPolicy.Check("abcdefghijkl", Current, UserName));
    }

    [Fact]
    public void CountsCharactersAsAPersonDoesNotAsUtf16Units()
    {
        // Eleven emoji are twenty-two UTF-16 units. Counting units would accept
        // this as long enough while a person counts eleven characters.
        string elevenEmoji = string.Concat(Enumerable.Repeat("\U0001F510", 11));

        Assert.Equal(22, elevenEmoji.Length);
        Assert.Equal(PasswordPolicyViolation.TooShort, AdministratorPasswordPolicy.Check(elevenEmoji, Current, UserName));
    }

    [Fact]
    public void RefusesMoreThanTheSignInFormAccepts()
    {
        // A password that can be set but not typed back in would lock the
        // administrator out of their own account.
        Assert.Equal(PasswordPolicyViolation.None,
            AdministratorPasswordPolicy.Check(new string('a', AdministratorPasswordPolicy.MaximumLength), Current, UserName));
        Assert.Equal(PasswordPolicyViolation.TooLong,
            AdministratorPasswordPolicy.Check(new string('a', AdministratorPasswordPolicy.MaximumLength + 1), Current, UserName));
    }

    [Fact]
    public void RefusesTheCurrentPasswordAgain() =>
        Assert.Equal(PasswordPolicyViolation.SameAsCurrent, AdministratorPasswordPolicy.Check(Current, Current, UserName));

    [Theory]
    [InlineData("my name is j.okafor ok")]
    [InlineData("J.OKAFOR and more words")]
    public void RefusesAPasswordContainingTheUserNameInAnyCase(string candidate) =>
        Assert.Equal(PasswordPolicyViolation.ContainsUserName, AdministratorPasswordPolicy.Check(candidate, Current, UserName));

    [Fact]
    public void DoesNotRefuseOnAUserNameTooShortToMatter()
    {
        // "al" occurs inside "totally normal". Refusing on it would reject good
        // passphrases for no gain.
        Assert.Equal(PasswordPolicyViolation.None,
            AdministratorPasswordPolicy.Check("totally normal passphrase", Current, "al"));
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("alllowercaseletters")]
    [InlineData("1234 5678 9012")]
    public void AcceptsLongPasswordsWithoutCompositionRules(string candidate)
    {
        // No "one uppercase, one symbol" requirement. Spaces are allowed, which
        // is what makes a long passphrase comfortable to type.
        Assert.Equal(PasswordPolicyViolation.None, AdministratorPasswordPolicy.Check(candidate, Current, UserName));
    }

    [Fact]
    public void ComparesTheCurrentPasswordExactly()
    {
        // Differing only in case is a different password; treating it as the same
        // would refuse a legitimate change and is not what "same" means here.
        Assert.Equal(PasswordPolicyViolation.None,
            AdministratorPasswordPolicy.Check(Current.ToUpperInvariant(), Current, UserName));
    }
}
