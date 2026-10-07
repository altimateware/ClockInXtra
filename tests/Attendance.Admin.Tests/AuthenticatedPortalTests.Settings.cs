using System.Text.RegularExpressions;
using Attendance.Admin.Models;
using Attendance.Application.Abstractions;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// The settings page offers the right control for each setting, with the
/// limits the database enforces, rather than a text box for everything.
/// </summary>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task SettingsOfferAListOfTimeZonesAndTypedInputs()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        string html = await client.GetStringAsync("/Settings", TestContext.Current.CancellationToken);

        // The business time zone is a list of the zones the DATABASE accepts,
        // with the current value selected — not a box to type an identifier in.
        Match zoneSelect = Regex.Match(
            html, """<select id="v-Attendance-BusinessTimeZoneId"[\s\S]*?</select>""");
        Assert.True(zoneSelect.Success, "the business time zone is not offered as a list");
        Assert.Contains("W. Central Africa Standard Time", zoneSelect.Value, StringComparison.Ordinal);
        Assert.True(Regex.Count(zoneSelect.Value, "<option ") > 100, "the list is not SQL Server's full zone list");

        string current = await QueryAsync<string>(
            "SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.BusinessTimeZoneId'", new { });
        Assert.Matches(
            $"""<option value="{Regex.Escape(current)}" selected="selected">""", zoneSelect.Value);

        // Times are time pickers.
        Assert.Matches(@"<input id=""v-Attendance-ClockInCloseTime"" name=""settingValue"" type=""time""", html);

        // Numbers carry the database's own bounds and unit.
        Assert.Matches(
            @"<input id=""v-Security-AdministratorLockoutThreshold"" name=""settingValue"" type=""number""[^>]*min=""3"" max=""20""", html);
        Assert.Matches(
            @"<input id=""v-Location-MaxAcceptedAccuracyMeters"" name=""settingValue"" type=""number""[^>]*step=""any""[^>]*min=""1"" max=""1000""", html);

        // Yes/no settings are a choice, not a box expecting the word "true".
        Assert.Matches(
            @"<fieldset class=""choice""[\s\S]*?name=""settingValue"" value=""true""[\s\S]*?name=""settingValue"" value=""false""", html);

        // Enum values read as words, while submitting the stored value.
        Assert.Contains(">Accept and flag late</option>", html, StringComparison.Ordinal);
        Assert.Contains(@"value=""AcceptAndFlagLate""", html, StringComparison.Ordinal);

        // A confirmed setting's confirm box starts ticked, so adjusting the
        // value does not silently withdraw the business's confirmation.
        Match zoneForm = Regex.Match(html, @"id=""v-Attendance-BusinessTimeZoneId""[\s\S]*?</form>");
        Assert.Contains(@"name=""confirm"" value=""true"" checked=""checked""", zoneForm.Value, StringComparison.Ordinal);

        // No setting is left as a free text box except where text is the value.
        Assert.DoesNotMatch(@"<input id=""v-(Attendance|Security|Location|Retention)-[^""]+"" name=""settingValue"" maxlength=""400""", html);
    }

    [Fact]
    public async Task AValueOutsideItsBoundsIsRefusedWithTheRule()
    {
        // The browser's min and max can be removed from a page; the database
        // enforces the same bounds, and the message says what they are.
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        (string? value, byte[] rowVersion) = await QueryAsync<(string?, byte[])>(
            "SELECT SettingValue, [RowVersion] FROM core.ApplicationSetting WHERE SettingKey = 'Security.AdministratorLockoutThreshold'",
            new { });

        await PostAsync(client, "/Settings/Set",
            [new("settingKey", "Security.AdministratorLockoutThreshold"),
             new("settingValue", "0"),
             new("rowVersion", Convert.ToBase64String(rowVersion))],
            formPath: "/Settings");

        string page = await client.GetStringAsync("/Settings", TestContext.Current.CancellationToken);

        Assert.Contains("Administrator lockout threshold was not saved: between 3 and 20 attempts.", page, StringComparison.Ordinal);
        Assert.Equal(value, await QueryAsync<string?>(
            "SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = 'Security.AdministratorLockoutThreshold'", new { }));
    }

    // ---- How inputs are chosen and described -------------------------------

    [Theory]
    [InlineData("Attendance.ClockInCloseTime", "Clock in close time")]
    [InlineData("Attendance.BusinessTimeZoneId", "Business time zone id")]
    [InlineData("Security.RequireAdministratorMfa", "Require administrator mfa")]
    [InlineData("Location.MaxAcceptedAccuracyMeters", "Max accepted accuracy meters")]
    public void SettingKeysBecomeReadableTitles(string key, string expected) =>
        Assert.Equal(expected, SettingInput.Title(key));

    [Theory]
    [InlineData("AcceptAndFlagLate", "Accept and flag late")]
    [InlineData("Reject", "Reject")]
    [InlineData("DistanceAndAccuracyThreshold", "Distance and accuracy threshold")]
    public void EnumValuesBecomeReadableLabels(string value, string expected) =>
        Assert.Equal(expected, SettingInput.Label(value));

    [Theory]
    [InlineData("timezone", null, SettingInput.Kind.TimeZone)]
    [InlineData("enum", "Reject,AcceptAndFlagLate", SettingInput.Kind.Choice)]
    [InlineData("bool", null, SettingInput.Kind.YesNo)]
    [InlineData("time", null, SettingInput.Kind.Time)]
    [InlineData("int", null, SettingInput.Kind.WholeNumber)]
    [InlineData("decimal", null, SettingInput.Kind.FractionalNumber)]
    [InlineData("string", null, SettingInput.Kind.Text)]
    public void EachDataTypeGetsItsOwnControl(string dataType, string? allowed, SettingInput.Kind expected) =>
        Assert.Equal(expected, SettingInput.KindOf(Row("Some.Setting", dataType, allowed: allowed)));

    [Fact]
    public void TheRuleNamesBoundsUnitAndWhatBlankMeans()
    {
        Assert.Equal(
            "between 1 and 36500 days, or blank for “Keep indefinitely”",
            SettingInput.Rule(Row("Retention.AttendanceDays", "int", min: 1m, max: 36500m, unit: "days", blank: "Keep indefinitely")));

        Assert.Equal(
            "between 3 and 20 attempts",
            SettingInput.Rule(Row("Security.MobileLockoutThreshold", "int", min: 3m, max: 20m, unit: "attempts")));
    }

    [Theory]
    [InlineData("08:30", "08:30")]
    [InlineData("23:59:00", "23:59")]
    [InlineData(null, null)]
    [InlineData("not a time", null)]
    public void StoredTimesAreGivenToTheTimePickerAsHoursAndMinutes(string? stored, string? expected) =>
        Assert.Equal(expected, SettingInput.TimeValue(stored));

    private static ApplicationSettingRow Row(
        string key,
        string dataType,
        string? allowed = null,
        decimal? min = null,
        decimal? max = null,
        string? unit = null,
        string? blank = null) =>
        new(key, null, dataType, "Test", "Test", allowed, min, max, unit, blank,
            RequiresBusinessConfirmation: true, IsUnset: true, IsMandatoryForAttendance: false,
            ConfirmedByUserName: null, UpdatedUtc: null, RowVersion: []);
}
