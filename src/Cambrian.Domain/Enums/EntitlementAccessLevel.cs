namespace Cambrian.Domain.Enums;

/// <summary>
/// Ranked access level. A higher integer automatically satisfies checks
/// for any lower level — License satisfies Download satisfies Stream.
/// Never renumber these values; HasAccessAsync uses (stored &gt;= required)
/// directly against the raw int.
/// </summary>
public enum EntitlementAccessLevel
{
    Stream = 1,
    Download = 2,
    License = 3,
    Admin = 4,
}
