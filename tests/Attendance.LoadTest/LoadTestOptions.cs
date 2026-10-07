using System.Globalization;

namespace Attendance.LoadTest;

/// <summary>Command-line options for one capacity run.</summary>
internal sealed record LoadTestOptions
{
    /// <summary>How the harness is invoked.</summary>
    public const string Usage = """
        Usage: dotnet run --project tests/Attendance.LoadTest -- [options]

          --employees   N   Virtual employees clocking in once each. Default 100.
          --concurrency N   How many are in flight at once. Default 16.
          --connection  S   Connection string. Defaults to the local development database.
          --help            This text.

        Never point this at production: it creates employees, widens the
        clock-in window for the duration of the run, and leaves audit entries
        that cannot be removed.
        """;

    /// <summary>Virtual employees, each clocking in once.</summary>
    public int Employees { get; private init; } = 100;

    /// <summary>Requests in flight at once.</summary>
    public int Concurrency { get; private init; } = 16;

    /// <summary>The database to run against.</summary>
    public string ConnectionString { get; private init; } =
        "Server=.;Database=ClockInXtra;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";

    /// <summary>True when the caller asked for the usage text.</summary>
    public bool ShowHelp { get; private init; }

    /// <summary>Parses the arguments, refusing anything it does not understand.</summary>
    /// <exception cref="ArgumentException">An argument is unknown or malformed.</exception>
    public static LoadTestOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        LoadTestOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    return options with { ShowHelp = true };

                case "--employees":
                    options = options with { Employees = PositiveNumber(args, ++i) };
                    break;

                case "--concurrency":
                    options = options with { Concurrency = PositiveNumber(args, ++i) };
                    break;

                case "--connection":
                    options = options with { ConnectionString = Value(args, ++i) };
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return options;
    }

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException($"{args[index - 1]} needs a value.");

    private static int PositiveNumber(string[] args, int index)
    {
        string value = Value(args, index);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number
            : throw new ArgumentException($"{args[index - 1]} needs a positive whole number, not '{value}'.");
    }
}
