using System.Net;
using Xunit;

namespace Attendance.Admin.Tests;

/// <summary>
/// Attendance corrections through the portal (DEC-08): request, the requester
/// refused as approver, a second administrator approving.
/// </summary>
/// <remarks>
/// The seeded roles split the duties (DEC-10: Attendance Administrator
/// requests, Super Administrator approves), but Super Administrator holds both
/// permissions so that the Attendance Administrator role stays grantable. The
/// rule that matters is therefore the database's, not the permission split, so
/// this test creates a throwaway role holding both permissions and proves that
/// even then the requester cannot approve their own request. The correction
/// switches are set for the test and put back exactly as they were.
/// </remarks>
public sealed partial class AuthenticatedPortalTests
{
    [Fact]
    public async Task ACorrectionNeedsASecondAdministratorAndKeepsTheOriginal()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string role = $"itest.corrections.{suffix}";
        string requester = $"itest.req.{suffix}";
        string approver = $"itest.appr.{suffix}";
        string employee = $"itest.corr.{suffix}";

        (string? allow, string? approval) = await QueryAsync<(string?, string?)>("""
            SELECT (SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.AllowCorrections'),
                   (SELECT SettingValue FROM core.ApplicationSetting WHERE SettingKey = 'Attendance.CorrectionsRequireApproval')
            """, new { });

        int mobileUserId = await CreateEmployeeAsync(employee);

        try
        {
            await ExecuteAsync("""
                UPDATE core.ApplicationSetting SET SettingValue = N'true'
                WHERE SettingKey IN ('Attendance.AllowCorrections', 'Attendance.CorrectionsRequireApproval');

                INSERT INTO core.Role (Name, Description, IsSystemRole, CreatedUtc)
                VALUES (@role, N'Integration test', 0, SYSUTCDATETIME());

                INSERT INTO core.RolePermission (RoleId, PermissionId, GrantedUtc)
                SELECT r.RoleId, p.PermissionId, SYSUTCDATETIME()
                FROM core.Role AS r
                CROSS JOIN core.Permission AS p
                WHERE r.Name = @role
                  AND p.Code IN ('Attendance.Correct', 'Attendance.ApproveCorrection', 'Report.View');

                INSERT INTO core.OfficeLocation (Name, Latitude, Longitude, AllowedRadiusMeters, Status)
                VALUES (CONCAT(N'ITEST ', @role), 6.465422, 3.406448, 5.00, 1);

                /* Yesterday, 09:00 UTC (10:00 in Lagos): the same date whatever the
                   configured zone, and a day already over — a missing clock-out. */
                DECLARE @day DATE = DATEADD(DAY, -1, CAST(SYSUTCDATETIME() AS DATE));
                INSERT INTO core.Attendance (MobileUserId, AttendanceDate, ClockInUtc, Status, ClockInOfficeLocationId)
                SELECT @id, @day, DATEADD(HOUR, 9, CAST(@day AS DATETIME2(3))), 1, o.OfficeLocationId
                FROM core.OfficeLocation AS o WHERE o.Name = CONCAT(N'ITEST ', @role);
                """,
                new { role, id = mobileUserId });

            await CreateAdministratorAsync(requester, role);
            await CreateAdministratorAsync(approver, role);

            Guid record = await QueryAsync<Guid>(
                "SELECT AttendancePublicId FROM core.Attendance WHERE MobileUserId = @id", new { id = mobileUserId });

            // 1. The requester asks for the missing clock-out to be set to 17:00.
            using HttpClient first = NewClient();
            await SignInAsync(first, requester, Password, CurrentCode());

            HttpResponseMessage form = await first.GetAsync($"/Corrections/Request/{record}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);

            HttpResponseMessage requested = await PostAsync(first, $"/Corrections/Request/{record}",
                [new("clockOut", "17:00"), new("reason", "Left without clocking out; confirmed by line manager")],
                formPath: $"/Corrections/Request/{record}");
            Assert.Equal(HttpStatusCode.Redirect, requested.StatusCode);

            (long correctionId, byte status, byte[] rowVersion) = await QueryAsync<(long, byte, byte[])>("""
                SELECT c.AttendanceCorrectionId, c.Status, c.[RowVersion]
                FROM core.AttendanceCorrection AS c
                INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId
                WHERE a.MobileUserId = @id
                """, new { id = mobileUserId });

            Assert.Equal(1, status);   // awaiting approval; the record is untouched
            Assert.Equal(1, await QueryAsync<int>("SELECT Status FROM core.Attendance WHERE MobileUserId = @id", new { id = mobileUserId }));

            // 2. The requester holds the approval permission too, and is still refused.
            await PostAsync(first, "/Corrections/Decide",
                [new("id", correctionId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 new("approve", "true"),
                 new("rowVersion", Convert.ToBase64String(rowVersion))],
                formPath: "/Corrections");

            string afterSelf = await first.GetStringAsync("/Corrections", TestContext.Current.CancellationToken);
            Assert.Contains("another administrator must decide it", afterSelf, StringComparison.Ordinal);
            Assert.Equal(1, await QueryAsync<int>(
                "SELECT Status FROM core.AttendanceCorrection WHERE AttendanceCorrectionId = @c", new { c = correctionId }));

            // 3. A second administrator approves; the record is corrected.
            using HttpClient second = NewClient();
            await SignInAsync(second, approver, Password, CurrentCode());

            HttpResponseMessage decided = await PostAsync(second, "/Corrections/Decide",
                [new("id", correctionId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                 new("approve", "true"),
                 new("rowVersion", Convert.ToBase64String(rowVersion))],
                formPath: "/Corrections");
            Assert.Equal(HttpStatusCode.Redirect, decided.StatusCode);

            (byte recordStatus, DateTime? clockOut, DateTime? originalOut) = await QueryAsync<(byte, DateTime?, DateTime?)>("""
                SELECT a.Status, a.ClockOutUtc, c.OriginalClockOutUtc
                FROM core.Attendance AS a
                INNER JOIN core.AttendanceCorrection AS c ON c.AttendanceId = a.AttendanceId
                WHERE a.MobileUserId = @id
                """, new { id = mobileUserId });

            Assert.Equal(3, recordStatus);   // Corrected
            Assert.NotNull(clockOut);
            Assert.Null(originalOut);        // what it said before is kept: no clock-out
        }
        finally
        {
            await ExecuteAsync("""
                DELETE c FROM core.AttendanceCorrection AS c
                INNER JOIN core.Attendance AS a ON a.AttendanceId = c.AttendanceId WHERE a.MobileUserId = @id;
                DELETE FROM core.AttendanceEvent WHERE MobileUserId = @id;
                DELETE FROM core.Attendance WHERE MobileUserId = @id;
                DELETE FROM core.OfficeLocation WHERE Name = CONCAT(N'ITEST ', @role);

                DELETE ar FROM core.AdministratorRole AS ar
                INNER JOIN core.Administrator AS a ON a.AdministratorId = ar.AdministratorId
                WHERE a.UserName IN (@requester, @approver);
                DELETE FROM core.AuthenticationAttempt WHERE SubjectType = 2 AND SubjectKey IN (@requester, @approver);
                DELETE FROM core.Administrator WHERE UserName IN (@requester, @approver);
                DELETE FROM core.Role WHERE Name = @role;

                UPDATE core.ApplicationSetting SET SettingValue = @allow WHERE SettingKey = 'Attendance.AllowCorrections';
                UPDATE core.ApplicationSetting SET SettingValue = @approval WHERE SettingKey = 'Attendance.CorrectionsRequireApproval';
                """,
                new { id = mobileUserId, role, requester, approver, allow, approval });

            await RemoveEmployeeAsync(mobileUserId);
        }
    }
}
