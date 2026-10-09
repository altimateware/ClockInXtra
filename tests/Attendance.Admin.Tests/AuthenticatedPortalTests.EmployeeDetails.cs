using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// DEC-11: every employee field is required, and department and job title are
/// chosen from lists that are maintained in the portal.
/// </summary>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task EveryEmployeeFieldIsRequiredAndDepartmentAndJobTitleAreLists()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string department = $"ITEST Dept {tag}";
        string jobTitle = $"ITEST Role {tag}";
        string userId = $"itest.emp.{tag}";

        await AddListEntriesAsync(department, jobTitle);

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            string page = await client.GetStringAsync("/Employees", TestContext.Current.CancellationToken);
            string form = Regex.Match(page, @"<form[^>]*class=""employee-form""[\s\S]*?</form>").Value;

            Assert.NotEmpty(form);
            Assert.DoesNotContain("optional", form, StringComparison.OrdinalIgnoreCase);

            // Department and job title are lists, required, offering the entries.
            Assert.Matches($@"<select id=""department"" name=""Department"" required>[\s\S]*?<option value=""{Regex.Escape(department)}""", form);
            Assert.Matches($@"<select id=""jobTitle"" name=""JobTitle"" required>[\s\S]*?<option value=""{Regex.Escape(jobTitle)}""", form);

            // Every text field is required too.
            foreach (string name in new[] { "UserId", "FirstName", "LastName", "EmployeeNumber", "Email", "PhoneNumber" })
            {
                Assert.Matches($@"<input[^>]*name=""{name}""[^>]*required", form);
            }

            // A submission without an email is refused, names what is missing,
            // and keeps what was typed.
            HttpResponseMessage refused = await PostAsync(client, "/Employees/Create",
                [new("UserId", userId), new("FirstName", "Ada"), new("LastName", "Obi"),
                 new("EmployeeNumber", $"E-{tag}"), new("PhoneNumber", "+234 803 123 4567"),
                 new("Department", department), new("JobTitle", jobTitle)],
                formPath: "/Employees");

            string refusedPage = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("An email address is required.", refusedPage, StringComparison.Ordinal);
            Assert.Contains($@"value=""{userId}""", refusedPage, StringComparison.Ordinal);
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM core.MobileUser WHERE UserId = @userId", new { userId }));

            // A department typed rather than chosen is refused by the database.
            HttpResponseMessage invented = await PostAsync(client, "/Employees/Create",
                [new("UserId", userId), new("FirstName", "Ada"), new("LastName", "Obi"),
                 new("EmployeeNumber", $"E-{tag}"), new("Email", "ada@example.com"),
                 new("PhoneNumber", "+234 803 123 4567"), new("Department", "Not On The List"), new("JobTitle", jobTitle)],
                formPath: "/Employees");

            Assert.Contains("must be chosen from their lists",
                await invented.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Equal(0, await QueryAsync<int>("SELECT COUNT(*) FROM core.MobileUser WHERE UserId = @userId", new { userId }));

            // Complete and chosen from the lists: created.
            HttpResponseMessage created = await PostAsync(client, "/Employees/Create",
                [new("UserId", userId), new("FirstName", "Ada"), new("LastName", "Obi"),
                 new("EmployeeNumber", $"E-{tag}"), new("Email", "ada@example.com"),
                 new("PhoneNumber", "+234 803 123 4567"), new("Department", department), new("JobTitle", jobTitle)],
                formPath: "/Employees");

            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            (string? storedDepartment, string? storedJobTitle) = await QueryAsync<(string?, string?)>(
                "SELECT Department, JobTitle FROM core.MobileUser WHERE UserId = @userId", new { userId });
            Assert.Equal(department, storedDepartment);
            Assert.Equal(jobTitle, storedJobTitle);
        }
        finally
        {
            await RemoveEmployeeWithCredentialAsync(userId);
            await RemoveListEntriesAsync(department, jobTitle);
        }
    }

    [Fact]
    public async Task EditingCompletesARecordAndKeepsAWithdrawnDepartment()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string department = $"ITEST Dept {tag}";
        string jobTitle = $"ITEST Role {tag}";
        string userId = $"itest.edit.{tag}";

        await AddListEntriesAsync(department, jobTitle);

        // A record from before every field was required: no email, phone or job
        // title — and a department that has since been withdrawn.
        int id = await QueryAsync<int>("""
            INSERT INTO core.MobileUser (UserId, EmployeeNumber, FirstName, LastName, Department, Status)
            VALUES (@userId, @number, N'Old', N'Record', @department, 1);
            UPDATE core.Department SET IsActive = 0 WHERE Name = @department;
            SELECT CAST(MobileUserId AS INT) FROM core.MobileUser WHERE UserId = @userId;
            """, new { userId, number = $"E-{tag}", department });

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            // The list says what is missing.
            string list = await client.GetStringAsync(
                $"/Employees?onlyNotReady=false&search={userId}", TestContext.Current.CancellationToken);
            Assert.Contains("Details missing: email, phone, job title.", list, StringComparison.Ordinal);

            // The withdrawn department is still offered to the employee who holds it.
            string edit = await client.GetStringAsync($"/Employees/Edit/{id}", TestContext.Current.CancellationToken);
            Assert.Matches($@"<option value=""{Regex.Escape(department)}"" selected=""selected"">\s*{Regex.Escape(department)} \(no longer offered\)", edit);

            Match rowVersion = Regex.Match(edit, @"name=""rowVersion"" value=""([^""]+)""");

            // Taken without checking before. An absent row version posts as
            // empty, which the procedure refuses as a concurrency conflict —
            // indistinguishable, from the status code alone, from the validation
            // refusal this test would otherwise be investigating.
            Assert.True(rowVersion.Success, "the edit form carried no row version");

            // Decoded as a browser would. The attribute holds base64, and Razor
            // writes a + as &#x2B;, which is not base64 — see AttributeValue.
            Assert.Equal(8, Convert.FromBase64String(AttributeValue(rowVersion)).Length);

            HttpResponseMessage saved = await PostAsync(client, $"/Employees/Edit/{id}",
                [new("rowVersion", AttributeValue(rowVersion)),
                 new("FirstName", "Old"), new("LastName", "Record"), new("EmployeeNumber", $"E-{tag}"),
                 new("Email", "old.record@example.com"), new("PhoneNumber", "08031234567"),
                 new("Department", department), new("JobTitle", jobTitle)],
                formPath: $"/Employees/Edit/{id}");

            await AssertRedirectedAsync(saved, $"Saving employee {id} with its withdrawn department");

            (string? email, string? phone, string? storedDepartment, string? storedJobTitle) =
                await QueryAsync<(string?, string?, string?, string?)>(
                    "SELECT Email, PhoneNumber, Department, JobTitle FROM core.MobileUser WHERE MobileUserId = @id", new { id });

            Assert.Equal("old.record@example.com", email);
            Assert.Equal("08031234567", phone);
            Assert.Equal(department, storedDepartment);
            Assert.Equal(jobTitle, storedJobTitle);
        }
        finally
        {
            await RemoveEmployeeWithCredentialAsync(userId);
            await RemoveListEntriesAsync(department, jobTitle);
        }
    }

    [Fact]
    public async Task RenamingADepartmentRenamesItForEveryoneWhoHoldsIt()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string department = $"ITEST Dept {tag}";
        string renamed = $"ITEST Renamed {tag}";
        string jobTitle = $"ITEST Role {tag}";
        string userId = $"itest.ren.{tag}";

        await AddListEntriesAsync(department, jobTitle);
        await ExecuteAsync("""
            INSERT INTO core.MobileUser (UserId, FirstName, LastName, Department, JobTitle, Status)
            VALUES (@userId, N'Ren', N'Amed', @department, @jobTitle, 1);
            """, new { userId, department, jobTitle });

        try
        {
            using HttpClient client = NewClient();
            await SignInAsync(client, _privilegedUser, Password, CurrentCode());

            (int entryId, byte[] rowVersion) = await QueryAsync<(int, byte[])>(
                "SELECT DepartmentId, [RowVersion] FROM core.Department WHERE Name = @department", new { department });

            await PostAsync(client, "/ReferenceLists/Rename",
                [new("list", "Department"), new("entryId", entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 new("rowVersion", Convert.ToBase64String(rowVersion)), new("name", renamed)],
                formPath: "/ReferenceLists");

            Assert.Equal(renamed, await QueryAsync<string>(
                "SELECT Department FROM core.MobileUser WHERE UserId = @userId", new { userId }));

            // A second entry differing only in capitals is refused.
            await PostAsync(client, "/ReferenceLists/Add",
                [new("list", "Department"), new("name", renamed.ToUpperInvariant())],
                formPath: "/ReferenceLists");

            Assert.Equal(1, await QueryAsync<int>(
                "SELECT COUNT(*) FROM core.Department WHERE UPPER(Name) = UPPER(@renamed)", new { renamed }));
        }
        finally
        {
            await RemoveEmployeeWithCredentialAsync(userId);
            await RemoveListEntriesAsync(renamed, jobTitle);
            await RemoveListEntriesAsync(department, jobTitle);
        }
    }

    private static Task AddListEntriesAsync(string department, string jobTitle) =>
        ExecuteAsync("""
            INSERT INTO core.Department (Name) VALUES (@department);
            INSERT INTO core.JobTitle (Name) VALUES (@jobTitle);
            """, new { department, jobTitle });

    private static Task RemoveListEntriesAsync(string department, string jobTitle) =>
        ExecuteAsync("""
            DELETE FROM core.Department WHERE Name = @department AND NOT EXISTS (SELECT 1 FROM core.MobileUser WHERE Department = @department);
            DELETE FROM core.JobTitle WHERE Name = @jobTitle AND NOT EXISTS (SELECT 1 FROM core.MobileUser WHERE JobTitle = @jobTitle);
            """, new { department, jobTitle });

    private static Task RemoveEmployeeWithCredentialAsync(string userId) =>
        ExecuteAsync("""
            DELETE c FROM core.EmployeeCredential AS c INNER JOIN core.MobileUser AS u ON u.MobileUserId = c.MobileUserId WHERE u.UserId = @userId;
            DELETE m FROM core.MfaCredential AS m INNER JOIN core.MobileUser AS u ON u.MobileUserId = m.MobileUserId WHERE u.UserId = @userId;
            DELETE FROM core.MobileUser WHERE UserId = @userId;
            """, new { userId });
}
