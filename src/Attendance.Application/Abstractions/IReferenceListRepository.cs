using Attendance.Domain.Enums;

namespace Attendance.Application.Abstractions;

/// <summary>
/// The lists an employee's department and job title are chosen from (DEC-11).
/// </summary>
/// <remarks>
/// Entries are renamed or deactivated, never deleted. A rename reaches every
/// employee who holds the entry (the database cascades it); a deactivation
/// stops the entry being chosen without rewriting anyone's record.
/// </remarks>
public interface IReferenceListRepository
{
    /// <summary>Every entry of a list, with how many employees hold each.</summary>
    Task<IReadOnlyList<ReferenceListEntry>> GetAllAsync(ReferenceList list, CancellationToken cancellationToken);

    /// <summary>Adds an entry.</summary>
    Task<AttendanceResultCode> AddAsync(
        ReferenceList list,
        string name,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Renames an entry, and with it every employee who holds it.</summary>
    Task<AttendanceResultCode> RenameAsync(
        ReferenceList list,
        int entryId,
        string name,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);

    /// <summary>Makes an entry available for choice again, or withdraws it.</summary>
    Task<AttendanceResultCode> SetActiveAsync(
        ReferenceList list,
        int entryId,
        bool isActive,
        byte[] rowVersion,
        int administratorId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

/// <summary>Which reference list.</summary>
public enum ReferenceList
{
    /// <summary>Departments.</summary>
    Department,

    /// <summary>Job titles.</summary>
    JobTitle,
}

/// <summary>One entry of a reference list.</summary>
/// <param name="EntryId">Identifier.</param>
/// <param name="Name">What employees hold, and reports show.</param>
/// <param name="IsActive">Whether it may be chosen.</param>
/// <param name="EmployeeCount">How many employees hold it.</param>
/// <param name="RowVersion">Concurrency token.</param>
public readonly record struct ReferenceListEntry(
    int EntryId,
    string Name,
    bool IsActive,
    int EmployeeCount,
    byte[] RowVersion);
