using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Attendance.Infrastructure.Deployment;

/// <summary>
/// The database scripts, embedded in this assembly, resolved into executable
/// batches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why embedded.</b> A published application has no <c>database</c> folder
/// beside it. Compiling the scripts into the assembly means the deployed
/// binaries and the schema they expect cannot be separated, which also removes
/// the usual failure where a server is updated and the scripts are not.
/// </para>
/// <para>
/// <b>Where the order comes from.</b> <c>database/deploy/01_run_all.sql</c>
/// already lists every script in dependency order, as <c>:r</c> includes. This
/// class reads that list rather than restating it in C#: a second copy of the
/// order would drift from the first, and the first is the one a database
/// administrator runs by hand from sqlcmd.
/// </para>
/// <para>
/// <b>What of sqlcmd is supported.</b> Only what these scripts use — <c>:r</c>,
/// <c>:setvar</c>, <c>$(variable)</c> and <c>:on error</c>. Anything else is
/// refused loudly rather than ignored, because a silently skipped directive
/// could mean a silently incomplete database.
/// </para>
/// </remarks>
internal sealed class SqlScriptSet
{
    /// <summary>Prefix every script resource is embedded under.</summary>
    private const string ResourcePrefix = "db/";

    private readonly Dictionary<string, string> _scripts;

    private SqlScriptSet(Dictionary<string, string> scripts) => _scripts = scripts;

    /// <summary>
    /// SHA-256 over every script in this assembly, name and content, in a fixed
    /// order. Two hosts built from the same binaries compute the same value, and
    /// any edit to any script changes it — which is what decides whether a
    /// database is already up to date.
    /// </summary>
    public byte[] Fingerprint { get; private init; } = [];

    /// <summary>Loads every embedded script.</summary>
    public static SqlScriptSet Load()
    {
        Assembly assembly = typeof(SqlScriptSet).Assembly;
        Dictionary<string, string> scripts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in assembly.GetManifestResourceNames())
        {
            string normalised = name.Replace('\\', '/');

            if (!normalised.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded script '{name}' could not be opened.");

            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            scripts[normalised[ResourcePrefix.Length..]] = reader.ReadToEnd();
        }

        if (scripts.Count == 0)
        {
            throw new InvalidOperationException(
                "No database scripts are embedded in Attendance.Infrastructure. The build is incomplete: "
                + "see the EmbeddedResource items in Attendance.Infrastructure.csproj.");
        }

        return new SqlScriptSet(scripts) { Fingerprint = ComputeFingerprint(scripts) };
    }

    /// <summary>The text of one script, by its path under <c>database/</c>.</summary>
    public string Text(string path)
    {
        string key = path.Replace('\\', '/');

        return _scripts.TryGetValue(key, out string? text)
            ? text
            : throw new InvalidOperationException(
                $"Embedded script 'database/{key}' is missing. Embedded scripts: "
                + string.Join(", ", _scripts.Keys.Order(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Expands a script into the batches to execute, resolving <c>:r</c>
    /// includes and substituting variables.
    /// </summary>
    /// <param name="path">Script path under <c>database/</c>.</param>
    /// <param name="variables">
    /// Values for <c>$(name)</c>. These win over any <c>:setvar</c> in the
    /// script, so the caller decides the database name rather than inheriting
    /// the default a script was written with.
    /// </param>
    public IReadOnlyList<SqlBatch> Expand(string path, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        Dictionary<string, string> resolved = new(variables, StringComparer.OrdinalIgnoreCase);
        List<SqlBatch> batches = [];
        StringBuilder current = new();
        string origin = path;

        Append(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Flush();

        return batches;

        void Append(string scriptPath, HashSet<string> including)
        {
            if (!including.Add(scriptPath))
            {
                throw new InvalidOperationException(
                    $"Script 'database/{scriptPath}' includes itself, directly or through another script.");
            }

            foreach (string raw in Lines(Text(scriptPath)))
            {
                string line = raw.TrimEnd();
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith(':'))
                {
                    // A directive ends the batch before it, because an include
                    // brings its own batches and a variable must be set before
                    // the text that uses it is sent.
                    Flush();
                    Directive(scriptPath, trimmed, including);
                    continue;
                }

                if (IsBatchSeparator(trimmed))
                {
                    Flush();
                    continue;
                }

                current.AppendLine(Substitute(line, scriptPath));
            }

            including.Remove(scriptPath);
        }

        void Directive(string scriptPath, string directive, HashSet<string> including)
        {
            if (directive.StartsWith(":r ", StringComparison.OrdinalIgnoreCase))
            {
                string included = Substitute(directive[3..].Trim().Trim('"'), scriptPath)
                    .Replace('\\', '/');

                string previousOrigin = origin;
                origin = included;
                Append(included, including);
                Flush();
                origin = previousOrigin;
                return;
            }

            if (directive.StartsWith(":setvar ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = directive[8..].Trim().Split(' ', 2, StringSplitOptions.TrimEntries);

                if (parts.Length == 2)
                {
                    // A caller-supplied value is not overwritten by the script's
                    // own default.
                    string name = parts[0];

                    if (!variables.ContainsKey(name))
                    {
                        resolved[name] = parts[1].Trim('"');
                    }
                }

                return;
            }

            if (directive.StartsWith(":on error", StringComparison.OrdinalIgnoreCase))
            {
                // ADO.NET throws on any error, which is stricter than
                // ':on error exit' and needs nothing here.
                return;
            }

            throw new InvalidOperationException(
                $"Script 'database/{scriptPath}' uses the sqlcmd directive '{directive}', which automatic "
                + "deployment does not support. Add support for it rather than letting it be skipped.");
        }

        string Substitute(string text, string scriptPath)
        {
            if (!text.Contains("$(", StringComparison.Ordinal))
            {
                return text;
            }

            StringBuilder result = new(text.Length);
            int index = 0;

            while (index < text.Length)
            {
                int start = text.IndexOf("$(", index, StringComparison.Ordinal);

                if (start < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                int end = text.IndexOf(')', start);

                if (end < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                result.Append(text, index, start - index);

                string name = text[(start + 2)..end];

                result.Append(resolved.TryGetValue(name, out string? value)
                    ? value
                    : throw new InvalidOperationException(
                        $"Script 'database/{scriptPath}' uses the variable $({name}), which has no value."));

                index = end + 1;
            }

            return result.ToString();
        }

        void Flush()
        {
            string text = current.ToString();
            current.Clear();

            if (!string.IsNullOrWhiteSpace(text))
            {
                batches.Add(new SqlBatch(origin, text));
            }
        }
    }

    /// <summary>
    /// Whether a line is a batch separator. Only a line that is nothing but
    /// <c>GO</c> counts, which is how sqlcmd reads it too: <c>GO</c> inside a
    /// string literal or a comment is on a line with other text.
    /// </summary>
    private static bool IsBatchSeparator(string trimmed) =>
        trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase)
        || (trimmed.StartsWith("GO ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[3..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _));

    private static IEnumerable<string> Lines(string text)
    {
        using StringReader reader = new(text);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static byte[] ComputeFingerprint(Dictionary<string, string> scripts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string name in scripts.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);

            // Line endings are normalised so that a checkout with different
            // settings does not look like a different script set.
            hash.AppendData(Encoding.UTF8.GetBytes(
                scripts[name].Replace("\r\n", "\n", StringComparison.Ordinal)));
            hash.AppendData([0]);
        }

        return hash.GetHashAndReset();
    }
}

/// <summary>One batch of SQL, and the script it came from.</summary>
/// <param name="Origin">Script path, for the message if the batch fails.</param>
/// <param name="Text">The statements between two batch separators.</param>
internal sealed record SqlBatch(string Origin, string Text);
