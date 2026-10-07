using System.Text.RegularExpressions;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// The office location page: typed inputs with the database's limits, edits
/// that keep what they do not show, and decimals stored exactly.
/// </summary>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task OfficeInputsAreTypedAndEditsKeepTheDescription()
    {
        string name = $"ITEST office {Guid.NewGuid():N}"[..40];

        int officeId = await QueryAsync<int>("""
            INSERT INTO core.OfficeLocation (Name, Description, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (@name, N'Third floor, west wing', 6.465422, 3.406448, 5.00, 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name });

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            string html = await client.GetStringAsync("/Locations", TestContext.Current.CancellationToken);
            string p = $"o{officeId}";

            // Coordinates and radius are numbers, bounded as the database bounds them.
            Assert.Matches($@"<input id=""{p}-lat"" name=""Latitude"" type=""number""[^>]*min=""-90"" max=""90"" step=""0.000001"" value=""6.465422""", html);
            Assert.Matches($@"<input id=""{p}-lon"" name=""Longitude"" type=""number""[^>]*min=""-180"" max=""180"" step=""0.000001"" value=""3.406448""", html);
            Assert.Matches($@"<input id=""{p}-radius"" name=""AllowedRadiusMeters"" type=""number""[^>]*min=""1"" max=""10000""[^>]*list=""radius-suggestions"" value=""5""", html);

            // The edit form carries the description, so saving cannot erase it.
            Assert.Matches($@"<textarea id=""{p}-description"" name=""Description""[^>]*>Third floor, west wing</textarea>", html);

            // Disabling asks for a reason.
            Assert.Matches($@"<input id=""{p}-reason"" name=""reason"" maxlength=""256"" required", html);

            // Save through the form, as a browser posts it: decimal point, six places.
            byte[] rowVersion = await QueryAsync<byte[]>(
                "SELECT [RowVersion] FROM core.OfficeLocation WHERE OfficeLocationId = @officeId", new { officeId });

            await PostAsync(client, "/Locations/Update",
                [new("officeLocationId", officeId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 new("rowVersion", Convert.ToBase64String(rowVersion)),
                 new("IsActive", "true"),
                 new("Name", name),
                 new("Description", "Third floor, west wing"),
                 new("Latitude", "6.465431"),
                 new("Longitude", "3.406457"),
                 new("AllowedRadiusMeters", "12.5")],
                formPath: "/Locations");

            (decimal lat, decimal lon, decimal radius, string? description) =
                await QueryAsync<(decimal, decimal, decimal, string?)>(
                    "SELECT Latitude, Longitude, AllowedRadiusMeters, Description FROM core.OfficeLocation WHERE OfficeLocationId = @officeId",
                    new { officeId });

            Assert.Equal(6.465431m, lat);
            Assert.Equal(3.406457m, lon);
            Assert.Equal(12.5m, radius);
            Assert.Equal("Third floor, west wing", description);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM core.OfficeLocation WHERE OfficeLocationId = @officeId;", new { officeId });
        }
    }

    [Fact]
    public async Task AnOfficeIsNotDisabledWithoutAReason()
    {
        string name = $"ITEST office {Guid.NewGuid():N}"[..40];

        int officeId = await QueryAsync<int>("""
            INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
            VALUES (@name, 6.465422, 3.406448, 5.00, 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name });

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            byte[] rowVersion = await QueryAsync<byte[]>(
                "SELECT [RowVersion] FROM core.OfficeLocation WHERE OfficeLocationId = @officeId", new { officeId });

            await PostAsync(client, "/Locations/SetStatus",
                [new("officeLocationId", officeId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 new("rowVersion", Convert.ToBase64String(rowVersion)),
                 new("isActive", "false"),
                 new("reason", "   ")],
                formPath: "/Locations");

            string page = await client.GetStringAsync("/Locations", TestContext.Current.CancellationToken);

            Assert.Contains("Give a reason for disabling the office.", page, StringComparison.Ordinal);
            Assert.Equal(1, await QueryAsync<int>(
                "SELECT Status FROM core.OfficeLocation WHERE OfficeLocationId = @officeId", new { officeId }));
        }
        finally
        {
            await ExecuteAsync("DELETE FROM core.OfficeLocation WHERE OfficeLocationId = @officeId;", new { officeId });
        }
    }
}
