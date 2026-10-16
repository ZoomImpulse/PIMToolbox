using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PIMToolbox.Core;

public sealed class GraphPimClient(HttpClient httpClient)
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    public async Task<IReadOnlyList<PimAssignment>> RetrieveAsync(
        TenantConfiguration tenant,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var user = await GetMeAsync(accessToken, cancellationToken).ConfigureAwait(false);

        var eligibleRoles = await GetPagedAsync(
            $"roleManagement/directory/roleEligibilityScheduleInstances?$filter=principalId eq '{user.Id}'&$expand=roleDefinition",
            accessToken, cancellationToken).ConfigureAwait(false);
        var activeRoles = await GetPagedAsync(
            $"roleManagement/directory/roleAssignmentScheduleInstances?$filter=principalId eq '{user.Id}'&$expand=roleDefinition",
            accessToken, cancellationToken).ConfigureAwait(false);
        var eligibleGroups = await GetPagedAsync(
            $"identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter=principalId eq '{user.Id}'&$expand=group",
            accessToken, cancellationToken).ConfigureAwait(false);
        var activeGroups = await GetPagedAsync(
            $"identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=principalId eq '{user.Id}'&$expand=group",
            accessToken, cancellationToken).ConfigureAwait(false);

        var roleSources = new List<(string GroupId, string GroupName, string RoleId, string ScopeId)>();
        if (eligibleRoles.Any(item => IsGroupBacked(item)) || eligibleGroups.Count > 0 || activeGroups.Count > 0)
        {
            var groups = await GetPagedAsync(
                $"users/{Uri.EscapeDataString(user.Id)}/transitiveMemberOf/microsoft.graph.group?$select=id,displayName,isAssignableToRole",
                accessToken, cancellationToken).ConfigureAwait(false);

            var roleGroupLookup = groups
                .Where(item => GetBoolean(item, "isAssignableToRole"))
                .Where(item => !string.IsNullOrWhiteSpace(GetString(item, "id")))
                .ToDictionary(item => GetString(item, "id")!, item => GetString(item, "displayName") ?? GetString(item, "id")!,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var group in eligibleGroups.Concat(activeGroups))
            {
                var groupId = GetString(group, "groupId");
                if (string.IsNullOrWhiteSpace(groupId)) continue;
                roleGroupLookup.TryAdd(groupId, GetNestedString(group, "group", "displayName") ?? groupId);
            }

            foreach (var (groupId, groupName) in roleGroupLookup)
            {
                var groupRoles = await GetPagedAsync(
                    $"roleManagement/directory/roleEligibilityScheduleInstances?$filter=principalId eq '{Uri.EscapeDataString(groupId)}'&$select=principalId,roleDefinitionId,directoryScopeId,memberType&$expand=roleDefinition($select=id,displayName)",
                    accessToken, cancellationToken).ConfigureAwait(false);
                foreach (var groupRole in groupRoles)
                {
                    var roleId = GetString(groupRole, "roleDefinitionId");
                    if (string.IsNullOrWhiteSpace(roleId)) continue;
                    roleSources.Add((groupId, groupName, roleId,
                        GetString(groupRole, "directoryScopeId") ?? "/"));
                }
            }
        }

        var groupBackedActiveRoles = new List<JsonElement>();
        foreach (var groupId in activeGroups.Select(item => GetString(item, "groupId"))
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var groupRoles = await GetPagedAsync(
                $"roleManagement/directory/roleAssignmentScheduleInstances?$filter=principalId eq '{Uri.EscapeDataString(groupId!)}'&$expand=roleDefinition",
                accessToken, cancellationToken).ConfigureAwait(false);
            groupBackedActiveRoles.AddRange(groupRoles);
        }
        activeRoles = activeRoles.Concat(groupBackedActiveRoles).ToArray();

        var groupAccessIds = eligibleGroups.Concat(activeGroups)
            .Select(item => (GroupId: GetString(item, "groupId"), AccessId: GetString(item, "accessId")))
            .Where(item => !string.IsNullOrWhiteSpace(item.GroupId) && !string.IsNullOrWhiteSpace(item.AccessId))
            .GroupBy(item => item.GroupId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.AccessId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var normalized = new List<PimAssignment>();
        AddRoleInstances(eligibleRoles, AssignmentStatus.Eligible, tenant, user.Id, roleSources, groupAccessIds, normalized);
        AddRoleInstances(activeRoles, AssignmentStatus.Active, tenant, user.Id, roleSources, groupAccessIds, normalized);
        AddGroupInstances(eligibleGroups, AssignmentStatus.Eligible, tenant, user.Id, normalized);
        AddGroupInstances(activeGroups, AssignmentStatus.Active, tenant, user.Id, normalized);
        return MergeRoleAssignments(normalized);
    }

    public async Task<TimeSpan?> GetMaximumActivationDurationAsync(
        TenantConfiguration tenant,
        PimAssignment assignment,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenant, assignment);
        var scopeType = assignment.Family == AssignmentFamily.Group ? "Group" : "DirectoryRole";
        var query = $"policies/roleManagementPolicyAssignments?$filter=scopeId eq '{EscapeOData(assignment.ResourceId)}' and scopeType eq '{scopeType}' and roleDefinitionId eq '{EscapeOData(assignment.Id)}'";
        var policies = await GetPagedAsync(query, accessToken, cancellationToken).ConfigureAwait(false);
        var policyId = policies.FirstOrDefault() is { } policy ? GetString(policy, "policyId") : null;
        if (policyId is null) return null;

        var rule = await GetAsync(
            $"policies/roleManagementPolicies/{Uri.EscapeDataString(policyId)}/rules/Expiration_EndUser_Assignment",
            accessToken, cancellationToken).ConfigureAwait(false);
        var duration = GetString(rule, "maximumDuration");
        return string.IsNullOrWhiteSpace(duration) ? null : DurationCalculator.ParseIso8601(duration);
    }

    public async Task<PimActionResult> ActivateAsync(
        TenantConfiguration tenant,
        PimAssignment assignment,
        string accessToken,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenant, assignment);
        if (assignment.Status != AssignmentStatus.Eligible)
            throw new InvalidOperationException("Only eligible assignments can be activated.");
        if (duration < TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration), "PIM activation duration must be at least five minutes.");

        var principalId = assignment.PrincipalId;
        var request = new Dictionary<string, object?>
        {
            ["action"] = "selfActivate",
            ["justification"] = justification,
            ["principalId"] = principalId,
            ["scheduleInfo"] = new
            {
                startDateTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                expiration = new { type = "afterDuration", duration = XmlDuration(duration) }
            }
        };

        if (assignment.Family == AssignmentFamily.Group)
        {
            request["groupId"] = assignment.ResourceId;
            request["accessId"] = assignment.Id;
            return await SubmitAndVerifyAsync(
                assignment,
                "identityGovernance/privilegedAccess/group/assignmentScheduleRequests",
                request,
                accessToken,
                activation: true,
                cancellationToken).ConfigureAwait(false);
        }

        if (assignment.IsGroupBacked)
        {
            var source = assignment.GroupSources is { Count: 1 } sources ? sources[0] : null;
            if (source is null || string.IsNullOrWhiteSpace(assignment.ActivationGroupAccessId))
                throw new InvalidOperationException(
                    "This role is group-backed. Activate its eligible source group first; a unique eligible group source could not be resolved.");

            request["groupId"] = source.GroupId;
            request["accessId"] = assignment.ActivationGroupAccessId;
            return await SubmitAndVerifyAsync(
                assignment,
                "identityGovernance/privilegedAccess/group/assignmentScheduleRequests",
                request,
                accessToken,
                activation: true,
                cancellationToken).ConfigureAwait(false);
        }

        request["roleDefinitionId"] = assignment.Id;
        request["directoryScopeId"] = assignment.ResourceId;
        return await SubmitAndVerifyAsync(
            assignment,
            "roleManagement/directory/roleAssignmentScheduleRequests",
            request,
            accessToken,
            activation: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PimActionResult> DeactivateAsync(
        TenantConfiguration tenant,
        PimAssignment assignment,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenant, assignment);
        if (assignment.Status != AssignmentStatus.Active)
            throw new InvalidOperationException("Only active assignments can be deactivated.");
        if (assignment.IsPermanent)
            throw new InvalidOperationException("Permanent active assignments cannot be deactivated by PIM Toolbox.");
        if (assignment.StartTime is { } startTime && DateTimeOffset.UtcNow - startTime < TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("PIM assignments cannot be deactivated during the first five minutes after activation.");

        var request = new Dictionary<string, object?>
        {
            ["action"] = "selfDeactivate",
            ["justification"] = "User initiated deactivation",
            ["principalId"] = assignment.PrincipalId
        };

        if (assignment.Family == AssignmentFamily.Group)
        {
            request["groupId"] = assignment.ResourceId;
            request["accessId"] = assignment.Id;
            return await SubmitAndVerifyAsync(
                assignment,
                "identityGovernance/privilegedAccess/group/assignmentScheduleRequests",
                request,
                accessToken,
                activation: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (assignment.IsGroupBacked)
        {
            var source = assignment.GroupSources is { Count: 1 } sources ? sources[0] : null;
            if (source is null || string.IsNullOrWhiteSpace(assignment.ActivationGroupAccessId))
                throw new InvalidOperationException(
                    "This role is group-backed, but its active source group is ambiguous or unavailable.");
            request["groupId"] = source.GroupId;
            request["accessId"] = assignment.ActivationGroupAccessId;
            return await SubmitAndVerifyAsync(
                assignment,
                "identityGovernance/privilegedAccess/group/assignmentScheduleRequests",
                request,
                accessToken,
                activation: false,
                cancellationToken).ConfigureAwait(false);
        }

        request["roleDefinitionId"] = assignment.Id;
        request["directoryScopeId"] = assignment.ResourceId;
        return await SubmitAndVerifyAsync(
            assignment,
            "roleManagement/directory/roleAssignmentScheduleRequests",
            request,
            accessToken,
            activation: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PimActionResult> SubmitAndVerifyAsync(
        PimAssignment assignment,
        string path,
        object request,
        string accessToken,
        bool activation,
        CancellationToken cancellationToken)
    {
        var response = await SendJsonAsync(HttpMethod.Post, path, accessToken, request, cancellationToken)
            .ConfigureAwait(false);
        var status = response is { ValueKind: JsonValueKind.Object } ? GetString(response.Value, "status") : null;
        if (status is not null &&
            (status.Equals("PendingApproval", StringComparison.OrdinalIgnoreCase) ||
             status.Equals("PendingAdminDecision", StringComparison.OrdinalIgnoreCase)))
            return new PimActionResult(status, Verified: false, PendingApproval: true);

        var targetGroupId = assignment.Family == AssignmentFamily.Group
            ? assignment.ResourceId
            : assignment.IsGroupBacked ? assignment.UniqueDependencyGroupId : null;
        var targetAccessId = assignment.Family == AssignmentFamily.Group
            ? assignment.Id
            : assignment.IsGroupBacked ? assignment.ActivationGroupAccessId : null;
        var visible = await VerifyAssignmentStateAsync(
            assignment, accessToken, activation, targetGroupId, targetAccessId, cancellationToken).ConfigureAwait(false);
        return new PimActionResult(status ?? (visible ? "Verified" : "AcceptedNotYetVisible"),
            visible,
            PendingApproval: false);
    }

    private async Task<bool> VerifyAssignmentStateAsync(
        PimAssignment assignment,
        string accessToken,
        bool shouldBePresent,
        string? targetGroupId,
        string? targetAccessId,
        CancellationToken cancellationToken)
    {
        var groupAction = assignment.Family == AssignmentFamily.Group || assignment.IsGroupBacked;
        var path = groupAction
            ? $"identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=principalId eq '{Uri.EscapeDataString(assignment.PrincipalId)}'&$select=groupId,accessId"
            : $"roleManagement/directory/roleAssignmentScheduleInstances?$filter=principalId eq '{Uri.EscapeDataString(assignment.PrincipalId)}'&$select=roleDefinitionId,directoryScopeId";

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var instances = await GetPagedAsync(path, accessToken, cancellationToken).ConfigureAwait(false);
            var exists = groupAction
                ? instances.Any(item =>
                    string.Equals(GetString(item, "groupId"), targetGroupId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(item, "accessId"), targetAccessId, StringComparison.OrdinalIgnoreCase))
                : instances.Any(item =>
                    string.Equals(GetString(item, "roleDefinitionId"), assignment.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(item, "directoryScopeId") ?? "/", assignment.ResourceId, StringComparison.OrdinalIgnoreCase));
            if (exists == shouldBePresent)
                return true;
            if (attempt < 19)
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<(string Id, string Username)> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        var user = await GetAsync("me?$select=id,userPrincipalName", accessToken, cancellationToken).ConfigureAwait(false);
        return (GetString(user, "id") ?? throw new InvalidDataException("Graph /me omitted id."),
            GetString(user, "userPrincipalName") ?? "");
    }

    private async Task<IReadOnlyList<JsonElement>> GetPagedAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var next = path;
        while (!string.IsNullOrWhiteSpace(next))
        {
            var page = await GetAsync(next, accessToken, cancellationToken).ConfigureAwait(false);
            if (page.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                items.AddRange(values.EnumerateArray().Select(value => value.Clone()));
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() ?? "" : "";
        }
        return items;
    }

    private async Task<JsonElement> GetAsync(string path, string accessToken, CancellationToken cancellationToken)
    {
        var uri = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{GraphRoot}/{path.TrimStart('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement?> SendJsonAsync(
        HttpMethod method,
        string path,
        string accessToken,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(method, $"{GraphRoot}/{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Microsoft Graph {method} {path} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}",
                null, response.StatusCode);

        if (response.Content.Headers.ContentLength == 0)
            return null;
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var clone = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await httpClient.SendAsync(clone, cancellationToken).ConfigureAwait(false);
            var canRetry =
                response.StatusCode == (HttpStatusCode)429 ||
                (request.Method == HttpMethod.Get &&
                 (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                  response.StatusCode == HttpStatusCode.GatewayTimeout));
            if (!canRetry || attempt >= 3)
                return response;

            var delay = response.Headers.RetryAfter?.Delta ??
                        TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            var content = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static void AddRoleInstances(
        IReadOnlyList<JsonElement> raw,
        AssignmentStatus status,
        TenantConfiguration tenant,
        string userId,
        IReadOnlyList<(string GroupId, string GroupName, string RoleId, string ScopeId)> sources,
        IReadOnlyDictionary<string, string[]> groupAccessIds,
        ICollection<PimAssignment> result)
    {
        var roles = raw.Select(item => new RolePimInstance(
            GetString(item, "roleDefinitionId"),
            GetString(item, "directoryScopeId"),
            GetString(item, "principalId") ?? userId,
            GetString(item, "memberType"),
            GetString(item, "assignmentType"),
            GetString(item, "endDateTime"),
            GetNestedString(item, "roleDefinition", "displayName"),
            StartDateTime: GetString(item, "startDateTime")));

        var assignments = roles.Select(item =>
        {
            var normalized = AssignmentNormalizer.NormalizeRole(tenant.TenantId, tenant.Name, item, status);
            var reconciled = GroupDependencyReconciler.AttachSources([normalized], sources).Single();
            var uniqueGroupId = reconciled.UniqueDependencyGroupId;
            var accessId = uniqueGroupId is not null &&
                           groupAccessIds.TryGetValue(uniqueGroupId, out var accessIds) &&
                           accessIds.Length == 1
                ? accessIds[0]
                : null;
            return reconciled with
            {
                PrincipalId = userId,
                ActivationGroupAccessId = accessId
            };
        });
        foreach (var assignment in assignments) result.Add(assignment);
    }

    private static void AddGroupInstances(
        IReadOnlyList<JsonElement> raw,
        AssignmentStatus status,
        TenantConfiguration tenant,
        string userId,
        ICollection<PimAssignment> result)
    {
        foreach (var item in raw)
        {
            result.Add(AssignmentNormalizer.NormalizeGroup(tenant.TenantId, tenant.Name,
                new GroupPimInstance(
                    GetString(item, "groupId"),
                    GetString(item, "accessId"),
                    GetString(item, "principalId") ?? userId,
                    GetString(item, "assignmentType"),
                    GetString(item, "endDateTime"),
                    GetNestedString(item, "group", "displayName"),
                    StartDateTime: GetString(item, "startDateTime")),
                status));
        }
    }

    private static IReadOnlyList<PimAssignment> MergeRoleAssignments(IReadOnlyList<PimAssignment> assignments)
    {
        var output = new List<PimAssignment>();
        foreach (var eligible in assignments.Where(item => item.Status == AssignmentStatus.Eligible))
        {
            var active = assignments.FirstOrDefault(item =>
                item.Status == AssignmentStatus.Active &&
                item.Family == eligible.Family &&
                item.Id.Equals(eligible.Id, StringComparison.OrdinalIgnoreCase) &&
                item.ResourceId.Equals(eligible.ResourceId, StringComparison.OrdinalIgnoreCase));
            output.Add(active is null
                ? eligible
                : eligible with { Status = AssignmentStatus.Active, EndTime = active.EndTime, StartTime = active.StartTime });
        }

        var represented = output.Select(item => $"{item.Family}|{item.Id}|{item.ResourceId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        output.AddRange(assignments.Where(item => item.Status == AssignmentStatus.Active &&
            !represented.Contains($"{item.Family}|{item.Id}|{item.ResourceId}")));
        return output;
    }

    private static bool IsGroupBacked(JsonElement item) =>
        GetString(item, "memberType") is { } memberType &&
        (memberType.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
         memberType.Equals("Inherited", StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetNestedString(JsonElement item, string parent, string property) =>
        item.TryGetProperty(parent, out var nested) ? GetString(nested, property) : null;

    private static bool GetBoolean(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string EscapeOData(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string XmlDuration(TimeSpan duration) =>
        System.Xml.XmlConvert.ToString(duration);

    private static void EnsureTenant(TenantConfiguration tenant, PimAssignment assignment)
    {
        if (!tenant.TenantId.Equals(assignment.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Assignment tenant does not match the tenant context. Cross-tenant actions are blocked.");
    }
}
