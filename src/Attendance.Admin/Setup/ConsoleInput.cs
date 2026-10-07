using System.Text;
using OtpNet;

namespace Attendance.Admin.Setup;

/// <summary>
/// Console prompts shared by the portal's two console commands: first
/// administrator setup and break-glass recovery.
/// </summary>
internal static class ConsoleInput
{
    /// <summary>Reads a line of ordinary input.</summary>
    public static string Prompt(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    /// <summary>Reads a value without echoing it.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Console.ReadKey(bool)"/> throws when console input has been
    /// redirected, which would make these commands unusable in exactly the
    /// situations a deployment meets them: an unattended provisioning script, a
    /// remote session without a real console, or a container. Rather than fail
    /// there, it falls back to a plain read and says out loud that the value will
    /// be visible.
    /// </para>
    /// <para>
    /// The fallback is still materially safer than the alternative these commands
    /// exist to avoid. A password passed as a command-line argument lands in
    /// shell history, in the process list where any local user can read it, and
    /// in the deployment log. Piped input does none of those.
    /// </para>
    /// </remarks>
    public static string PromptSecret(string label)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine(
                $"{label}: (input is redirected — it will not be hidden, and the account " +
                "must change its password at first sign-in)");

            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        Console.Write($"{label}: ");

        StringBuilder value = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            // Control characters are ignored rather than accumulated, so an
            // arrow key does not silently become part of the password.
            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// Shows an authenticator enrolment once, so the operator can scan it.
    /// </summary>
    /// <remarks>
    /// This is the only moment the secret is legible. It is not written to the
    /// audit trail, the application log or any error message (§13, §32, §33) —
    /// only to the console in front of the person running the command. Whoever
    /// runs it with output redirected to a file has just put a TOTP secret in
    /// that file, so the warning is printed rather than implied.
    /// </remarks>
    public static void ShowAuthenticator(string userName, byte[] secret)
    {
        string base32 = Base32Encoding.ToString(secret).TrimEnd('=');

        Console.WriteLine();
        Console.WriteLine("Scan this with an authenticator application NOW. It is shown once.");
        Console.WriteLine("Do not capture this output to a log.");
        Console.WriteLine();
        Console.WriteLine(
            $"  otpauth://totp/ClockInXtra:{Uri.EscapeDataString(userName)}" +
            $"?secret={base32}&issuer=ClockInXtra&algorithm=SHA1&digits=6&period=30");
        Console.WriteLine();
        Console.WriteLine($"  Manual entry: {base32}");
        Console.WriteLine();
    }
}
