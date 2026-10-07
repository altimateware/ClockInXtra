using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// Administrator accounts and their roles, managed from the portal (§17, §42).
/// </summary>
/// <remarks>
/// <para>
/// <b>The authority rules live in the database, not here.</b> Every changing
/// method is backed by a procedure that runs
/// <c>admin.usp_Administrator_CheckAuthorityOver</c>: nobody acts on themselves,
/// and nobody acts on an administrator holding a permission they lack. Changes
/// that would leave no one able to manage administrators are refused with
/// <see cref="AttendanceResultCode.LastAdministratorManager"/>. Keeping those
/// rules below this interface means a second code path — a script, a future
/// API — cannot quietly skip them.
/// </para>
/// <para>
/// The portal still hides actions it knows will be refused, as a courtesy. That
/// is presentation; the refusal is the control.
/// </para>
/// </remarks>
public interface IAdministratorManagementRepository
{
    /// <summary>Lists administrators, with their roles.</summary>
    /// <param name="searchTerm">Matched literally against user name, display name and email.</param>
    Task<IReadOnlyList<AdministratorSummary>> SearchAsync(string? searchTerm, CancellationToken cancellationToken);

    /// <summary>Lists every role with the permissions it grants.</summary>
    /// <param name="actingAdministratorId">
    /// When given, each role reports whether this administrator could grant it —
    /// whether they already hold every permission it carries.
    /// </param>
    Task<IReadOnlyList<RoleSummary>> GetRolesAsync(int? actingAdministratorId, CancellationToken cancellationToken);

    /// <summary>Creates an administrator account.</summary>
    /// <param name="administrator">Profile details.</param>
    /// <param name="password">The issued password, already hashed.</param>
    /// <param name="roleId">An initial role, or none.</param>
    /// <param name="actingAdministratorId">Who is creating it.</param>
    /// <param name="correlationId">Ties the audit row to the request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// The account is created with <c>MustChangePassword</c> set and no
    /// authenticator. The caller enrols one straight afterwards through
    /// <see cref="IAdministratorRepository.EnrolMfaAsync"/>; if that second step
    /// fails, the account exists but cannot sign in, and the authenticator can be
    /// enrolled again from the portal. That is recoverable, unlike the first
    /// administrator's case, which is why only setup enrols in the same statement.
    /// </remarks>
    Task<CreateAdministratorResult> CreateAsync(
        NewAdministrator administrator,
        PasswordHash password,
        int? roleId,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Activates or deactivates an administrator.</summary>
    /// <param name="reason">Required when deactivating; it is audited.</param>
    Task<AttendanceResultCode> SetStatusAsync(
        int administratorId,
        bool active,
        string? reason,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Grants or removes a role.</summary>
    Task<AttendanceResultCode> SetRoleAsync(
        int administratorId,
        int roleId,
        bool grant,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues another administrator a new password, ending their sessions and
    /// clearing their sign-in lockout.
    /// </summary>
    Task<AttendanceResultCode> ResetPasswordAsync(
        int administratorId,
        PasswordHash password,
        int actingAdministratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>Profile details for a new administrator.</summary>
/// <param name="UserName">The name they will sign in with.</param>
/// <param name="DisplayName">Shown in the portal and the audit trail.</param>
/// <param name="Email">Contact address, where recorded.</param>
public readonly record struct NewAdministrator(string UserName, string DisplayName, string? Email);

/// <summary>The outcome of creating an administrator.</summary>
/// <param name="ResultCode">Success, or why not.</param>
/// <param name="AdministratorId">Internal identifier, on success.</param>
/// <param name="AdministratorPublicId">External identifier, on success.</param>
public readonly record struct CreateAdministratorResult(
    AttendanceResultCode ResultCode,
    int AdministratorId,
    Guid AdministratorPublicId);

/// <summary>An administrator, as the management page lists them.</summary>
/// <param name="AdministratorId">Internal identifier.</param>
/// <param name="AdministratorPublicId">External identifier.</param>
/// <param name="UserName">The name they sign in with.</param>
/// <param name="DisplayName">Shown in the portal.</param>
/// <param name="Email">Contact address.</param>
/// <param name="IsActive">Whether the account is active.</param>
/// <param name="MfaStatus">Whether an authenticator is enrolled and proven.</param>
/// <param name="MustChangePassword">Whether they still owe a password change.</param>
/// <param name="LastLoginUtc">Last successful sign-in.</param>
/// <param name="CreatedUtc">When the account was created.</param>
/// <param name="CanSignIn">
/// Active and, where the policy requires one, holding an enrolled authenticator.
/// The single answer to "could this person get in right now".
/// </param>
/// <param name="Roles">Roles held.</param>
public readonly record struct AdministratorSummary(
    int AdministratorId,
    Guid AdministratorPublicId,
    string UserName,
    string DisplayName,
    string? Email,
    bool IsActive,
    AdministratorMfaStatus MfaStatus,
    bool MustChangePassword,
    DateTime? LastLoginUtc,
    DateTime CreatedUtc,
    bool CanSignIn,
    IReadOnlyList<RoleReference> Roles);

/// <summary>A role held by an administrator.</summary>
/// <param name="RoleId">Identifier.</param>
/// <param name="Name">Name.</param>
public readonly record struct RoleReference(int RoleId, string Name);

/// <summary>A role and exactly what it grants.</summary>
/// <param name="RoleId">Identifier.</param>
/// <param name="Name">Name.</param>
/// <param name="Description">What it is for.</param>
/// <param name="IsSystemRole">Seeded with the system rather than created locally.</param>
/// <param name="AssignableByActor">
/// Whether the administrator asking could grant it. Presentation only; the
/// database refuses an escalating grant regardless.
/// </param>
/// <param name="Permissions">The permissions the role carries.</param>
public readonly record struct RoleSummary(
    int RoleId,
    string Name,
    string? Description,
    bool IsSystemRole,
    bool AssignableByActor,
    IReadOnlyList<PermissionSummary> Permissions);

/// <summary>A permission, as shown beside a role.</summary>
/// <param name="Code">The policy name the portal authorizes against.</param>
/// <param name="Category">Grouping for display.</param>
/// <param name="Description">What it allows.</param>
public readonly record struct PermissionSummary(string Code, string Category, string Description);
