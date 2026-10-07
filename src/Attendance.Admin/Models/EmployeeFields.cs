namespace Attendance.Admin.Models;

/// <summary>
/// The employee detail fields, shared by the add and edit forms so the two
/// cannot drift apart (DEC-11: every field required).
/// </summary>
public sealed class EmployeeFields
{
    /// <summary>Payroll or HR number.</summary>
    public string? EmployeeNumber { get; init; }

    /// <summary>Given name.</summary>
    public string? FirstName { get; init; }

    /// <summary>Family name.</summary>
    public string? LastName { get; init; }

    /// <summary>Contact address.</summary>
    public string? Email { get; init; }

    /// <summary>Contact number.</summary>
    public string? PhoneNumber { get; init; }

    /// <summary>Selected department.</summary>
    public string? Department { get; init; }

    /// <summary>Selected job title.</summary>
    public string? JobTitle { get; init; }

    /// <summary>Active departments to choose from.</summary>
    public IReadOnlyList<string> Departments { get; init; } = [];

    /// <summary>Active job titles to choose from.</summary>
    public IReadOnlyList<string> JobTitles { get; init; } = [];

    /// <summary>
    /// A department the employee already holds, offered even when it has been
    /// deactivated, so an unrelated edit does not force them off it.
    /// </summary>
    public string? KeepDepartment { get; init; }

    /// <summary>The job title held, on the same terms.</summary>
    public string? KeepJobTitle { get; init; }

    /// <summary>Whether the signed-in administrator may maintain the lists.</summary>
    public bool CanManageLists { get; init; }

    /// <summary>The choices for one list: active entries, plus the one held if it is not among them.</summary>
    public static IReadOnlyList<string> WithKept(IReadOnlyList<string> active, string? kept) =>
        string.IsNullOrWhiteSpace(kept) || active.Contains(kept, StringComparer.Ordinal)
            ? active
            : [.. active, kept];
}
