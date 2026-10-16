using System.Globalization;
using System.Xml;

namespace PIMToolbox.Core;

public sealed record TenantConfiguration(
    string Name,
    string TenantId,
    string ClientId,
    string EdgeExecutable,
    string EdgeUserDataDirectory,
    string EdgeProfileDirectory = "Default",
    string? ExpectedUsername = null);

public sealed record TenantIdentity(string TenantId, string UserId, string Username, string DisplayName);

public enum AssignmentFamily { DirectoryRole, Group }
public enum AssignmentStatus { Eligible, Active }

public sealed record PimRoleSource(string GroupId, string GroupName, string? AccessId = null);

public sealed record PimAssignment(
    string TenantId,
    string TenantName,
    AssignmentFamily Family,
    string Id,
    string ResourceId,
    string PrincipalId,
    string DisplayName,
    AssignmentStatus Status,
    string? MemberType = null,
    string? AssignmentType = null,
    DateTimeOffset? EndTime = null,
    IReadOnlyList<PimRoleSource>? GroupSources = null,
    DateTimeOffset? StartTime = null)
{
    public string? ActivationGroupAccessId { get; init; }
    public bool IsPermanent => Status == AssignmentStatus.Active && EndTime is null;

    public bool IsGroupBacked => Family == AssignmentFamily.DirectoryRole &&
        MemberType is not null &&
        (MemberType.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
         MemberType.Equals("Inherited", StringComparison.OrdinalIgnoreCase));

    public string? UniqueDependencyGroupId =>
        GroupSources is { Count: 1 } ? GroupSources[0].GroupId : null;
}

public sealed record TenantSnapshot(
    string TenantId,
    IReadOnlyList<PimAssignment> Assignments,
    DateTimeOffset RetrievedAt,
    string? Error = null)
{
    public bool Succeeded => Error is null;
}

public sealed record TenantRefreshResult(TenantSnapshot Snapshot, bool KeptPreviousData);

public sealed record RolePimInstance(
    string? RoleDefinitionId,
    string? DirectoryScopeId,
    string? PrincipalId,
    string? MemberType,
    string? AssignmentType,
    string? EndDateTime,
    string? RoleDisplayName,
    string? Id = null,
    string? StartDateTime = null);

public sealed record GroupPimInstance(
    string? GroupId,
    string? AccessId,
    string? PrincipalId,
    string? AssignmentType,
    string? EndDateTime,
    string? GroupDisplayName,
    string? Id = null,
    string? StartDateTime = null);

public sealed record PimActionResult(string Status, bool Verified, bool PendingApproval);

public static class AssignmentNormalizer
{
    public static PimAssignment NormalizeRole(
        string tenantId,
        string tenantName,
        RolePimInstance instance,
        AssignmentStatus status,
        IReadOnlyList<PimRoleSource>? groupSources = null)
    {
        if (string.IsNullOrWhiteSpace(instance.RoleDefinitionId))
            throw new InvalidDataException("Role PIM instance is missing roleDefinitionId.");

        return new PimAssignment(
            tenantId,
            tenantName,
            AssignmentFamily.DirectoryRole,
            instance.RoleDefinitionId,
            string.IsNullOrWhiteSpace(instance.DirectoryScopeId) ? "/" : instance.DirectoryScopeId,
            instance.PrincipalId ?? "",
            instance.RoleDisplayName ?? $"Role {instance.RoleDefinitionId}",
            status,
            instance.MemberType,
            instance.AssignmentType,
            ParseDate(instance.EndDateTime),
            groupSources ?? [],
            ParseDate(instance.StartDateTime));
    }

    public static PimAssignment NormalizeGroup(
        string tenantId,
        string tenantName,
        GroupPimInstance instance,
        AssignmentStatus status)
    {
        if (string.IsNullOrWhiteSpace(instance.GroupId) || string.IsNullOrWhiteSpace(instance.AccessId))
            throw new InvalidDataException("Group PIM instance is missing groupId or accessId.");

        return new PimAssignment(
            tenantId,
            tenantName,
            AssignmentFamily.Group,
            instance.AccessId,
            instance.GroupId,
            instance.PrincipalId ?? "",
            $"{instance.GroupDisplayName ?? instance.GroupId} ({instance.AccessId})",
            status,
            AssignmentType: instance.AssignmentType,
            EndTime: ParseDate(instance.EndDateTime),
            StartTime: ParseDate(instance.StartDateTime));
    }

    public static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}

public static class GroupDependencyReconciler
{
    public static IReadOnlyList<PimAssignment> AttachSources(
        IEnumerable<PimAssignment> roles,
        IEnumerable<(string GroupId, string GroupName, string RoleId, string ScopeId)> roleGroupAssignments)
    {
        var sources = roleGroupAssignments
            .GroupBy(item => Key(item.RoleId, item.ScopeId))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PimRoleSource>)group
                    .GroupBy(item => item.GroupId, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new PimRoleSource(item.Key, item.First().GroupName))
                    .ToArray());

        return roles.Select(role =>
        {
            if (!role.IsGroupBacked)
                return role;

            if (sources.TryGetValue(Key(role.Id, role.ResourceId), out var matches) && matches.Count > 0)
                return role with { GroupSources = matches };

            if (role.MemberType!.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(role.PrincipalId))
                return role with { GroupSources = [new PimRoleSource(role.PrincipalId, "Role group")] };

            return role with { GroupSources = [] };
        }).ToArray();
    }

    private static string Key(string roleId, string scopeId) => $"{roleId}|{scopeId}";
}

public sealed class TenantSnapshotStore
{
    private readonly Dictionary<string, TenantSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<TenantSnapshot> Snapshots => _snapshots.Values;

    public TenantRefreshResult ApplyRefresh(string tenantId, IReadOnlyList<PimAssignment>? assignments, Exception? error)
    {
        if (error is not null)
        {
            var previous = _snapshots.GetValueOrDefault(tenantId);
            var failed = new TenantSnapshot(
                tenantId,
                previous?.Assignments ?? [],
                previous?.RetrievedAt ?? DateTimeOffset.MinValue,
                error.Message);
            _snapshots[tenantId] = failed;
            return new TenantRefreshResult(failed, previous is not null);
        }

        var snapshot = new TenantSnapshot(tenantId, assignments ?? [], DateTimeOffset.UtcNow);
        _snapshots[tenantId] = snapshot;
        return new TenantRefreshResult(snapshot, false);
    }

    public IReadOnlyList<PimAssignment> GetMergedAssignments() =>
        _snapshots.Values.SelectMany(snapshot => snapshot.Assignments)
            .ToArray();
}

public static class DurationCalculator
{
    public static TimeSpan ParseIso8601(string duration) => XmlConvert.ToTimeSpan(duration);

    public static TimeSpan Suggest(
        TimeSpan maximum,
        TimeOnly workStart,
        TimeOnly workEnd,
        DateTimeOffset now)
    {
        if (maximum <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (workEnd <= workStart)
            throw new ArgumentException("Work end must be later than work start.");

        var current = TimeOnly.FromDateTime(now.LocalDateTime);
        var remainingWorkDay = current >= workStart && current < workEnd
            ? workEnd.ToTimeSpan() - current.ToTimeSpan()
            : maximum;
        return remainingWorkDay < maximum ? remainingWorkDay : maximum;
    }

    public static TimeSpan? GetRemaining(DateTimeOffset? end, DateTimeOffset now)
    {
        if (end is null) return null;
        var remaining = end.Value - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static string GetUrgency(DateTimeOffset? end, DateTimeOffset now)
    {
        var remaining = GetRemaining(end, now);
        if (remaining is null || remaining <= TimeSpan.Zero) return "None";
        if (remaining < TimeSpan.FromMinutes(15)) return "Critical";
        if (remaining < TimeSpan.FromHours(1)) return "Warning";
        return "Safe";
    }
}
