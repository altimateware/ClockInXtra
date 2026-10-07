using System.Globalization;
using System.Text;
using Attendance.Application.Abstractions;

namespace Attendance.Admin.Models;

/// <summary>
/// How each setting is presented for input: which control, with which limits.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is derived from the setting's own row — its data type, its
/// allowed values, and the bounds and unit held beside it — so a new setting gets
/// the right control without touching this class, and the limits a browser
/// applies are the same ones the database enforces.
/// </para>
/// <para>
/// The browser's checks are convenience only. <c>admin.usp_ApplicationSetting_Set</c>
/// validates every value again, whatever a modified page submits.
/// </para>
/// </remarks>
public static class SettingInput
{
    /// <summary>The data type whose value is a time zone identifier.</summary>
    public const string TimeZoneType = "timezone";

    /// <summary>The kind of control a setting is edited with.</summary>
    public enum Kind
    {
        /// <summary>A list of named choices.</summary>
        Choice,

        /// <summary>A list of time zones.</summary>
        TimeZone,

        /// <summary>Yes or no.</summary>
        YesNo,

        /// <summary>A time of day.</summary>
        Time,

        /// <summary>A whole number.</summary>
        WholeNumber,

        /// <summary>A number that may have a fractional part.</summary>
        FractionalNumber,

        /// <summary>A version such as 1.4.0.</summary>
        Version,

        /// <summary>Free text: anything not covered above.</summary>
        Text,
    }

    /// <summary>Picks the control for a setting.</summary>
    public static Kind KindOf(ApplicationSettingRow setting) =>
        setting.DataType switch
        {
            TimeZoneType => Kind.TimeZone,
            "enum" when !string.IsNullOrWhiteSpace(setting.AllowedValues) => Kind.Choice,
            "bool" => Kind.YesNo,
            "time" => Kind.Time,
            "int" => Kind.WholeNumber,
            "decimal" => Kind.FractionalNumber,
            _ when setting.SettingKey == "Mobile.MinimumAppVersion" => Kind.Version,
            _ => Kind.Text,
        };

    /// <summary>The permitted values of a choice setting.</summary>
    public static IReadOnlyList<string> Choices(ApplicationSettingRow setting) =>
        (setting.AllowedValues ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A heading for a setting: "Attendance.ClockInCloseTime" becomes
    /// "Clock in close time". The key itself is still shown beneath it, because
    /// it is what the documentation and the audit trail use.
    /// </summary>
    public static string Title(string settingKey)
    {
        ArgumentNullException.ThrowIfNull(settingKey);

        int dot = settingKey.LastIndexOf('.');
        return Words(dot >= 0 ? settingKey[(dot + 1)..] : settingKey);
    }

    /// <summary>
    /// A readable label for an enum value: "AcceptAndFlagLate" becomes
    /// "Accept and flag late". The stored value is unchanged.
    /// </summary>
    public static string Label(string value) => Words(value);

    /// <summary>A bound as the browser expects it: invariant, no trailing zeros.</summary>
    public static string? Bound(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The value as a time input expects it (HH:mm), from a stored value that
    /// may carry seconds.
    /// </summary>
    public static string? TimeValue(string? stored) =>
        TimeOnly.TryParse(stored, CultureInfo.InvariantCulture, out TimeOnly time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// The rule a value must satisfy, in words: shown under the input, and in
    /// the message when a value is refused.
    /// </summary>
    public static string Rule(ApplicationSettingRow setting)
    {
        string unit = string.IsNullOrWhiteSpace(setting.Unit) ? string.Empty : " " + setting.Unit;

        string rule = KindOf(setting) switch
        {
            Kind.WholeNumber or Kind.FractionalNumber when setting.MinValue is not null && setting.MaxValue is not null =>
                $"between {Bound(setting.MinValue)} and {Bound(setting.MaxValue)}{unit}",
            Kind.WholeNumber or Kind.FractionalNumber when setting.MinValue is not null =>
                $"at least {Bound(setting.MinValue)}{unit}",
            Kind.WholeNumber or Kind.FractionalNumber when setting.MaxValue is not null =>
                $"at most {Bound(setting.MaxValue)}{unit}",
            Kind.WholeNumber => "a whole number" + unit,
            Kind.FractionalNumber => "a number" + unit,
            Kind.Version => "a version such as 1.4.0",
            Kind.Time => "a time of day",
            Kind.Choice => "one of the listed choices",
            Kind.TimeZone => "a time zone from the list",
            Kind.YesNo => "yes or no",
            _ => "a value",
        };

        return setting.BlankMeaning is { Length: > 0 } meaning
            ? $"{rule}, or blank for “{meaning}”"
            : rule;
    }

    private static string Words(string pascal)
    {
        StringBuilder words = new(pascal.Length + 8);

        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];

            // A capital starts a new word, except inside a run of capitals
            // ("SDK" stays together) unless the next letter is lower case.
            bool newWord = i > 0 && char.IsUpper(c)
                && (!char.IsUpper(pascal[i - 1]) || (i + 1 < pascal.Length && char.IsLower(pascal[i + 1])));

            if (newWord)
            {
                words.Append(' ');
            }

            words.Append(i == 0 ? c : (newWord && !IsAcronymAt(pascal, i) ? char.ToLowerInvariant(c) : c));
        }

        return words.ToString();
    }

    private static bool IsAcronymAt(string text, int index) =>
        index + 1 < text.Length && char.IsUpper(text[index + 1]);
}
