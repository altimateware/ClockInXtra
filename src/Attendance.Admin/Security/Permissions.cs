using System.Security.Claims;
using Attendance.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace Attendance.Admin.Security;

/// <summary>
/// The permission codes seeded in <c>core.Permission</c>.
/// </summary>
/// <remarks>
/// Constants rather than literals scattered through controllers. §42 asks for
/// authorization to be centralised rather than re-tested in each action, so a
/// permission is named once here, turned into a policy once at startup, and
/// applied as an attribute.
/// </remarks>
public static class Permissions
{
    /// <summary>View employee records.</summary>
    public const string MobileUserView = "MobileUser.View";

    /// <summary>Create and amend employee records.</summary>
    public const string MobileUserManage = "MobileUser.Manage";

    /// <summary>View administrator accounts.</summary>
    public const string AdministratorView = "Administrator.View";

    /// <summary>Create and amend administrator accounts.</summary>
    public const string AdministratorManage = "Administrator.Manage";

    /// <summary>Assign roles and permissions.</summary>
    public const string RoleManage = "Role.Manage";

    /// <summary>View registered devices.</summary>
    public const string DeviceView = "Device.View";

    /// <summary>Approve a pending device registration.</summary>
    public const string DeviceApprove = "Device.Approve";

    /// <summary>Revoke a device.</summary>
    public const string DeviceRevoke = "Device.Revoke";

    /// <summary>View office locations.</summary>
    public const string OfficeLocationView = "OfficeLocation.View";

    /// <summary>Create and amend office locations.</summary>
    public const string OfficeLocationManage = "OfficeLocation.Manage";

    /// <summary>View configuration settings.</summary>
    public const string SettingView = "Setting.View";

    /// <summary>Change configuration settings.</summary>
    public const string SettingManage = "Setting.Manage";

    /// <summary>Enrol an authenticator.</summary>
    public const string MfaEnrol = "Mfa.Enrol";

    /// <summary>Reset or revoke an authenticator.</summary>
    public const string MfaReset = "Mfa.Reset";

    /// <summary>View attendance records.</summary>
    public const string AttendanceView = "Attendance.View";

    /// <summary>Request an attendance correction.</summary>
    public const string AttendanceCorrect = "Attendance.Correct";

    /// <summary>Approve an attendance correction.</summary>
    public const string AttendanceApproveCorrection = "Attendance.ApproveCorrection";

    /// <summary>View reports.</summary>
    public const string ReportView = "Report.View";

    /// <summary>View the audit trail.</summary>
    public const string AuditView = "Audit.View";

    /// <summary>The claim type carrying a granted permission.</summary>
    public const string ClaimType = "clockinxtra:permission";

    /// <summary>Every permission, for policy registration.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MobileUserView, MobileUserManage,
        AdministratorView, AdministratorManage, RoleManage,
        DeviceView, DeviceApprove, DeviceRevoke,
        OfficeLocationView, OfficeLocationManage,
        SettingView, SettingManage,
        MfaEnrol, MfaReset,
        AttendanceView, AttendanceCorrect, AttendanceApproveCorrection,
        ReportView, AuditView,
    ];
}

/// <summary>
/// Claim types the portal's authentication cookie carries.
/// </summary>
public static class AdministratorClaims
{
    /// <summary>The internal administrator identifier.</summary>
    public const string AdministratorId = "clockinxtra:administratorId";

    /// <summary>
    /// The security stamp as it stood when the cookie was issued.
    /// </summary>
    /// <remarks>
    /// Compared against the stored stamp on every request. A password change, a
    /// disabled account or an altered role assignment rotates the stamp and every
    /// existing session stops working at once — rather than a signed cookie
    /// remaining valid until it happens to expire (threat TH-35).
    /// </remarks>
    public const string SecurityStamp = "clockinxtra:securityStamp";

    /// <summary>Whether a password change is required before anything else.</summary>
    public const string MustChangePassword = "clockinxtra:mustChangePassword";

    /// <summary>Builds the claims principal for a signed-in administrator.</summary>
    public static ClaimsPrincipal CreatePrincipal(AdministratorRecord administrator, string authenticationScheme)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, administrator.AdministratorPublicId.ToString()),
            new Claim(ClaimTypes.Name, administrator.UserName),
            new Claim(AdministratorId, administrator.AdministratorId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(SecurityStamp, administrator.SecurityStamp.ToString()),
            new Claim(MustChangePassword, administrator.MustChangePassword ? "true" : "false"),
        ];

        if (!string.IsNullOrWhiteSpace(administrator.DisplayName))
        {
            claims.Add(new Claim("displayName", administrator.DisplayName));
        }

        // Permissions become claims, so authorization is a policy check rather
        // than a role-name comparison inside an action (§42).
        claims.AddRange(administrator.Permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationScheme));
    }
}

/// <summary>
/// Registers one authorization policy per permission.
/// </summary>
public static class PermissionPolicies
{
    /// <summary>Adds a policy for every permission code.</summary>
    public static void AddAll(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (string permission in Permissions.All)
        {
            options.AddPolicy(permission, policy =>
                policy.RequireAuthenticatedUser()
                      .RequireClaim(Permissions.ClaimType, permission));
        }

        // Nothing is reachable without authentication unless an action opts out.
        // A portal that defaulted to anonymous would put every page one missing
        // attribute away from being public.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
}
