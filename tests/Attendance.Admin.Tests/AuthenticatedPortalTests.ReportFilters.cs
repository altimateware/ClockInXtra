using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Report filters keep each label with its control, and offer employees,
/// departments and event types as lists rather than blank boxes.
/// </summary>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task AttendanceFiltersGroupLabelsAndSuggestEmployees()
    {
        string employee = $"itest.filter.{Guid.NewGuid():N}"[..40];
        int mobileUserId = await CreateEmployeeAsync(employee);

        try
        {
            // An employee's department must be a list entry (DEC-11).
            await ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM core.Department WHERE Name = N'ITEST Finance')
                    INSERT INTO core.Department (Name) VALUES (N'ITEST Finance');
                UPDATE core.MobileUser SET Department = N'ITEST Finance' WHERE MobileUserId = @id;
                """,
                new { id = mobileUserId });

            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            string html = await client.GetStringAsync("/Reports/Attendance", TestContext.Current.CancellationToken);

            Assert.Contains(@"class=""filters""", html, StringComparison.Ordinal);

            // Every label is inside the same box as its control: the layout
            // cannot separate them, whatever the width.
            foreach (string id in new[] { "from", "to", "userId", "department", "officeLocationId", "status", "exception" })
            {
                Assert.Matches($@"<div class=""filter"">\s*<label for=""{id}"">", html);
            }

            // Employees and departments are suggested, the new employee among them.
            Assert.Matches(@"<input id=""userId"" name=""userId""[^>]*list=""employee-options""", html);
            Assert.Contains($@"<option value=""{employee}"">", html, StringComparison.Ordinal);
            Assert.Contains(@"<option value=""ITEST Finance"">", html, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveEmployeeAsync(mobileUserId);
            await ExecuteAsync(
                "DELETE FROM core.Department WHERE Name = N'ITEST Finance' AND NOT EXISTS (SELECT 1 FROM core.MobileUser WHERE Department = N'ITEST Finance');");
        }
    }

    [Fact]
    public async Task AuditEventTypesAreAList()
    {
        using HttpClient client = NewClient();
        await SignInAsync(client, _privilegedUser, Password, CurrentCode());

        string html = await client.GetStringAsync("/Reports/Audit", TestContext.Current.CancellationToken);

        // Signing in above wrote at least this event type to the ledger.
        Assert.Matches(@"<select id=""eventType"" name=""eventType"">[\s\S]*?<option value=""Administrator.LoginSucceeded""", html);
        Assert.Matches(@"<input id=""correlationId""[^>]*pattern=", html);
    }
}
