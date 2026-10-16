using System.Net;
using System.Text.Json;
using PIMToolbox.Core;
using Xunit;

namespace PIMToolbox.Core.Tests;

public class DomainTests
{
    private static TenantConfiguration Tenant(string id, string name) =>
        new(name, id, "e8045933-dcbc-4e12-93d1-0cd98ae27917", "edge", Path.GetTempPath());

    [Fact]
    public void NormalizesRolesAndGroupsWithTenantAndResourceIdentity()
    {
        var role = AssignmentNormalizer.NormalizeRole("tenant-a", "Alpha",
            new RolePimInstance("role-id", null, "user-id", "Direct", "Activated", null, "Reader"),
            AssignmentStatus.Eligible);
        var group = AssignmentNormalizer.NormalizeGroup("tenant-b", "Beta",
            new GroupPimInstance("group-id", "member", "user-id", null, null, "Operators"),
            AssignmentStatus.Active);

        Assert.Equal("tenant-a", role.TenantId);
        Assert.Equal("/", role.ResourceId);
        Assert.Equal(AssignmentFamily.Group, group.Family);
        Assert.Equal("member", group.Id);
        Assert.Equal("tenant-b", group.TenantId);
    }

    [Fact]
    public void ReconcilesUniqueAndAmbiguousGroupBackedRoleSources()
    {
        var unique = AssignmentNormalizer.NormalizeRole("tenant-a", "Alpha",
            new RolePimInstance("role", "/", "user", "Inherited", null, null, "Role"), AssignmentStatus.Eligible);
        var multiple = unique with { Id = "role-2" };
        var sources = new[]
        {
            ("group-1", "Group One", "role", "/"),
            ("group-2", "Group Two", "role-2", "/"),
            ("group-3", "Group Three", "role-2", "/")
        };

        var result = GroupDependencyReconciler.AttachSources([unique, multiple], sources);

        Assert.Equal("group-1", result[0].UniqueDependencyGroupId);
        Assert.Null(result[1].UniqueDependencyGroupId);
        Assert.Equal(2, result[1].GroupSources!.Count);
    }

    [Fact]
    public void FailedRefreshKeepsPriorTenantDataAndMergedViewExcludesFailureStatusOnly()
    {
        var store = new TenantSnapshotStore();
        var retained = new PimAssignment("tenant-a", "Alpha", AssignmentFamily.Group,
            "member", "group-a", "user-a", "Operators", AssignmentStatus.Eligible);
        store.ApplyRefresh("tenant-a", [retained], null);
        var otherTenant = retained with { TenantId = "tenant-b", TenantName = "Beta", ResourceId = "group-b" };
        store.ApplyRefresh("tenant-b", [otherTenant], null);

        var failed = store.ApplyRefresh("tenant-a", null, new HttpRequestException("offline"));

        Assert.True(failed.KeptPreviousData);
        Assert.Single(failed.Snapshot.Assignments);
        Assert.Equal("offline", failed.Snapshot.Error);
        Assert.Equal(2, store.GetMergedAssignments().Count);
        Assert.Equal(2, store.Snapshots.Count);
    }

    [Fact]
    public void DurationUsesWorkdayRemainderAndPolicyMaximum()
    {
        var localTime = new DateTime(2026, 10, 15, 10, 0, 0);
        var now = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        var suggested = DurationCalculator.Suggest(TimeSpan.FromHours(8),
            new TimeOnly(8, 0), new TimeOnly(17, 0), now);

        Assert.Equal(TimeSpan.FromHours(7), suggested);
        Assert.Equal(TimeSpan.FromHours(2), DurationCalculator.ParseIso8601("PT2H"));
        Assert.Equal("Critical", DurationCalculator.GetUrgency(now.AddMinutes(10), now));
    }

    [Fact]
    public void EdgeLaunchUsesExplicitPerTenantProfileArguments()
    {
        var tenant = new TenantConfiguration("Alpha", "tenant-a", "app-id", @"C:\Edge\msedge.exe",
            @"C:\Users\user\AppData\Local\PIMToolbox\Edge\tenant-a", "Profile 2", "admin@alpha.example");
        var startInfo = EdgeProfileLauncher.CreateStartInfo(new Uri("https://login.microsoftonline.com/authorize"), tenant);

        Assert.Equal(tenant.EdgeExecutable, startInfo.FileName);
        Assert.Contains(@"--user-data-dir=C:\Users\user\AppData\Local\PIMToolbox\Edge\tenant-a", startInfo.ArgumentList);
        Assert.Contains("--profile-directory=Profile 2", startInfo.ArgumentList);
        Assert.Equal("https://login.microsoftonline.com/authorize", startInfo.ArgumentList[^1]);
    }

    [Fact]
    public void GroupBackedRoleAndGroupShareActivationTimestampKey()
    {
        var role = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.DirectoryRole, "role-a", "/", "user-a",
            "Privileged Role", AssignmentStatus.Active, "Inherited",
            GroupSources: [new PimRoleSource("group-a", "Operators")])
        {
            ActivationGroupAccessId = "member"
        };
        var group = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.Group, "member", "group-a", "user-a",
            "Operators (member)", AssignmentStatus.Active);

        Assert.Contains(
            PimToolboxSettingsStore.ActivationKeys(group),
            key => PimToolboxSettingsStore.ActivationKeys(role).Contains(key));
    }

    [Fact]
    public async Task MigratesLegacyPreferencesAndFavoriteKeysWithoutChangingSourceFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pimtoolbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var legacyPath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(legacyPath, """
            {
              "Tenants": [{ "Name": "Alpha", "Id": "tenant-a" }],
              "FavoriteRoles": [{ "Id": "role-a", "TenantId": "tenant-a", "ResourceId": "/" }],
              "GlobalJustification": "Approved work",
              "WorkStartTime": "09:00",
              "WorkEndTime": "18:00"
            }
            """);
        try
        {
            var settings = await new PimToolboxSettingsStore(directory).LoadAsync();

            Assert.Single(settings.Tenants);
            Assert.Equal("tenant-a", settings.Tenants[0].TenantId);
            Assert.Contains("tenant-a|role-a|/", settings.FavoriteKeys);
            Assert.Equal("Approved work", settings.GlobalJustification);
            Assert.Equal("09:00", settings.WorkStartTime);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(Path.Combine(directory, "settings.v2.json")));

            var activationKey = PimToolboxSettingsStore.ActivationKeys(new PimAssignment(
                "tenant-a", "Alpha", AssignmentFamily.Group, "member", "group-a", "user-a",
                "Operators", AssignmentStatus.Active)).Single();
            settings.ActivationStartTimes[activationKey] = DateTimeOffset.UtcNow;
            var store = new PimToolboxSettingsStore(directory);
            await store.SaveAsync(settings);
            var reloaded = await store.LoadAsync();
            Assert.Contains(activationKey, reloaded.ActivationStartTimes.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActionRouterUsesAssignmentTenantTokenAndBlocksUnknownTenant()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var tenants = new[] { Tenant("tenant-a", "Alpha"), Tenant("tenant-b", "Beta") };
        var requestedTenant = "";
        var router = new TenantActionRouter(tenants, (tenant, _) =>
        {
            requestedTenant = tenant.TenantId;
            return Task.FromResult($"token-{tenant.TenantId}");
        }, graph);
        var assignment = new PimAssignment("tenant-b", "Beta", AssignmentFamily.Group,
            "member", "group-b", "user-b", "Operators", AssignmentStatus.Eligible);

        var result = await router.ActivateAsync(assignment, TimeSpan.FromHours(1), "test");

        Assert.Equal("tenant-b", requestedTenant);
        Assert.Equal("token-tenant-b", handler.AuthorizationToken);
        Assert.Contains("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests", handler.PostRequestUri);
        Assert.True(result.Verified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.ActivateAsync(
            assignment with { TenantId = "tenant-c" }, TimeSpan.FromHours(1), "test"));
    }

    [Fact]
    public async Task GroupBackedRoleActivationRoutesToItsSourceGroup()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var assignment = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.DirectoryRole, "role-id", "/", "user-a",
            "Privileged Role", AssignmentStatus.Eligible, "Inherited",
            GroupSources: [new PimRoleSource("source-group", "Source Group")])
        {
            ActivationGroupAccessId = "member"
        };

        handler.VerificationGroupId = "source-group";
        var result = await graph.ActivateAsync(Tenant("tenant-a", "Alpha"), assignment, "tenant-token",
            TimeSpan.FromHours(1), "test");

        Assert.Contains("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests", handler.PostRequestUri);
        Assert.DoesNotContain("/roleManagement/directory/roleAssignmentScheduleRequests", handler.PostRequestUri);
        Assert.Contains("\"groupId\":\"source-group\"", handler.RequestBody);
        Assert.Contains("\"accessId\":\"member\"", handler.RequestBody);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task DeactivationIsBlockedDuringFirstFiveMinutes()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var assignment = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.Group, "member", "group-a", "user-a",
            "Operators", AssignmentStatus.Active, EndTime: DateTimeOffset.UtcNow.AddHours(1),
            StartTime: DateTimeOffset.UtcNow.AddMinutes(-4));

        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.DeactivateAsync(
            Tenant("tenant-a", "Alpha"), assignment, "tenant-token"));
        Assert.Null(handler.PostRequestUri);
    }

    [Fact]
    public async Task PermanentActiveAssignmentCannotBeDeactivated()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var assignment = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.Group, "member", "group-a", "user-a",
            "Operators", AssignmentStatus.Active, StartTime: DateTimeOffset.UtcNow.AddHours(-1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.DeactivateAsync(
            Tenant("tenant-a", "Alpha"), assignment, "tenant-token"));
        Assert.Null(handler.PostRequestUri);
    }

    [Fact]
    public async Task DeactivationIsVerifiedAfterAssignmentDisappears()
    {
        var handler = new RecordingHandler { VerificationAssignmentPresent = false };
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var assignment = new PimAssignment(
            "tenant-a", "Alpha", AssignmentFamily.Group, "member", "group-a", "user-a",
            "Operators", AssignmentStatus.Active, EndTime: DateTimeOffset.UtcNow.AddHours(1),
            StartTime: DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await graph.DeactivateAsync(Tenant("tenant-a", "Alpha"), assignment, "tenant-token");

        Assert.True(result.Verified);
        Assert.Equal("Granted", result.Status);
    }

    [Fact]
    public async Task PendingApprovalIsReportedWithoutClaimingVerification()
    {
        var handler = new RecordingHandler { PostStatus = "PendingApproval" };
        using var http = new HttpClient(handler);
        var graph = new GraphPimClient(http);
        var assignment = new PimAssignment("tenant-a", "Alpha", AssignmentFamily.Group,
            "member", "group-a", "user-a", "Operators", AssignmentStatus.Eligible);

        var result = await graph.ActivateAsync(Tenant("tenant-a", "Alpha"), assignment, "tenant-token",
            TimeSpan.FromHours(1), "test");

        Assert.True(result.PendingApproval);
        Assert.False(result.Verified);
        Assert.Equal(0, handler.GetRequests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string VerificationGroupId { get; set; } = "group-b";
        public bool VerificationAssignmentPresent { get; set; } = true;
        public string PostStatus { get; set; } = "Granted";
        public int GetRequests { get; private set; }
        public string? AuthorizationToken { get; private set; }
        public string? PostRequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        value = VerificationAssignmentPresent
                            ? new[] { new { groupId = VerificationGroupId, accessId = "member" } }
                            : []
                    }))
                });
            }

            AuthorizationToken = request.Headers.Authorization?.Parameter;
            PostRequestUri = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id = "request-id", status = PostStatus }))
            });
        }
    }
}
