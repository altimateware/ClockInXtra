using System.Globalization;

namespace Attendance.Application.Services;

/// <summary>
/// The rules a password an administrator chooses must meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length, not composition.</b> NIST SP 800-63B (§5.1.1.2) recommends a
/// minimum length and explicitly advises against composition rules — "one
/// uppercase, one digit, one symbol" — because they push people towards
/// predictable substitutions (<c>Password1!</c>) without adding real strength.
/// So there are none here. Spaces are allowed, which is what makes a long
/// passphrase comfortable to type.
/// </para>
/// <para>
/// <b>Twelve characters</b> is the floor the first-administrator setup already
/// enforces for an account that holds every permission. Using the same number
/// everywhere avoids a stronger password being demanded at setup than for the
/// same account afterwards.
/// </para>
/// <para>
/// <b>Not checked, and why:</b> breached-password lists. 800-63B recommends
/// comparing against known-compromised values, but the usual sources are online
/// services, which §2.1 rules out. An organisation that maintains an offline copy
/// can add the check here; until then this is a recorded gap rather than an
/// implied control.
/// </para>
/// <para>
/// Employee passwords are not governed by this: how employees obtain and change
/// passwords is OPEN-01 and OPEN-40.
/// </para>
/// </remarks>
public static class AdministratorPasswordPolicy
{
    /// <summary>Fewest characters accepted.</summary>
    public const int MinimumLength = 12;

    /// <summary>
    /// Most characters accepted. Matches the sign-in form's limit, so a password
    /// that can be set can always be typed back in.
    /// </summary>
    public const int MaximumLength = 256;

    /// <summary>
    /// Shortest user name that is checked for inside the password. A two-letter
    /// name such as "al" occurs inside ordinary words, and refusing on it would
    /// reject good passphrases for no gain.
    /// </summary>
    public const int MinimumUserNameLengthToCheck = 4;

    /// <summary>Checks a proposed new password.</summary>
    /// <param name="newPassword">The password being set.</param>
    /// <param name="currentPassword">The password being replaced, as typed.</param>
    /// <param name="userName">The account's user name.</param>
    /// <returns>The first rule broken, or <see cref="PasswordPolicyViolation.None"/>.</returns>
    public static PasswordPolicyViolation Check(string? newPassword, string? currentPassword, string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            return PasswordPolicyViolation.Blank;
        }

        // Counted in text elements, so a character outside the Basic Multilingual
        // Plane — which .NET stores as two UTF-16 units — counts once, the way a
        // person counts it.
        if (new StringInfo(newPassword).LengthInTextElements < MinimumLength)
        {
            return PasswordPolicyViolation.TooShort;
        }

        if (newPassword.Length > MaximumLength)
        {
            return PasswordPolicyViolation.TooLong;
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            return PasswordPolicyViolation.SameAsCurrent;
        }

        string trimmedUserName = userName.Trim();

        if (trimmedUserName.Length >= MinimumUserNameLengthToCheck
            && newPassword.Contains(trimmedUserName, StringComparison.OrdinalIgnoreCase))
        {
            return PasswordPolicyViolation.ContainsUserName;
        }

        return PasswordPolicyViolation.None;
    }
}

/// <summary>Which password rule was broken.</summary>
public enum PasswordPolicyViolation
{
    /// <summary>The password meets every rule.</summary>
    None = 0,

    /// <summary>Empty or only whitespace.</summary>
    Blank,

    /// <summary>Fewer than <see cref="AdministratorPasswordPolicy.MinimumLength"/> characters.</summary>
    TooShort,

    /// <summary>More than <see cref="AdministratorPasswordPolicy.MaximumLength"/> characters.</summary>
    TooLong,

    /// <summary>Identical to the password it replaces.</summary>
    SameAsCurrent,

    /// <summary>Contains the account's own user name.</summary>
    ContainsUserName,
}
