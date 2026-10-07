using System.Security.Cryptography;
using System.Text;

namespace Attendance.Admin.Security;

/// <summary>
/// Produces the one-time password an employee is issued at creation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the system chooses it rather than the administrator.</b> An initial
/// password typed by whoever is creating accounts tends to be the same one every
/// time, or a pattern derived from the employee's name — and here it is handed
/// over verbally or on paper, so it is seen by at least two people before it is
/// ever used. Generating it removes the choice, and with it the pattern.
/// </para>
/// <para>
/// <b>It is shown exactly once</b>, on the page that follows creation, and never
/// stored anywhere in the clear: the database receives only the PBKDF2 hash. If
/// it is lost before it reaches the employee, the account needs a new one rather
/// than a lookup.
/// </para>
/// <para>
/// <b>The alphabet is deliberately reduced.</b> Characters that look alike in a
/// handwritten note or a sans-serif font — 0/O, 1/l/I, 5/S, 2/Z — are excluded,
/// because an employee mistyping a password they were given on paper is the
/// failure this control causes most often. Removing them costs a little entropy
/// and the length is set to keep the total comfortably high: 20 characters over
/// a 50-character alphabet is about 112 bits, far beyond what any offline attack
/// on a 220,000-iteration PBKDF2 hash could reach.
/// </para>
/// <para>
/// This is a transitional control. OPEN-01 has not settled whether employee
/// passwords ultimately come from the organisation's own directory, and OPEN-40
/// leaves the mobile password-change flow undecided. Until both are answered,
/// an employee keeps the password issued here, which is recorded as a known
/// weakness rather than presented as a finished design.
/// </para>
/// </remarks>
public static class InitialPassword
{
    /// <summary>
    /// Unambiguous characters only. See the remarks for what is missing and why.
    /// </summary>
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyzACDEFGHJKLMNPQRTUVWXY346789";

    /// <summary>How many characters an issued password has.</summary>
    public const int Length = 20;

    /// <summary>Generates a password.</summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/> selects
    /// uniformly without the modulo bias that a hand-rolled
    /// <c>random % alphabet.Length</c> introduces — which would quietly make some
    /// characters likelier than others and shrink the real key space.
    /// </remarks>
    public static string Generate()
    {
        Span<char> buffer = stackalloc char[Length];

        RandomNumberGenerator.GetItems<char>(Alphabet, buffer);

        // Grouped for reading aloud and for copying off a screen accurately.
        StringBuilder grouped = new(Length + (Length / 5));

        for (int i = 0; i < buffer.Length; i++)
        {
            if (i > 0 && i % 5 == 0)
            {
                grouped.Append('-');
            }

            grouped.Append(buffer[i]);
        }

        return grouped.ToString();
    }
}
