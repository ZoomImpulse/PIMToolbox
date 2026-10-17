using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PIMToolbox.Core;
using PIMToolbox.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PIMToolbox.Core.Tests.HeadlessTestApplication))]

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
        var retainedAt = store.Snapshots.Single(snapshot => snapshot.TenantId == "tenant-a").RetrievedAt;
        var otherTenant = retained with { TenantId = "tenant-b", TenantName = "Beta", ResourceId = "group-b" };
        store.ApplyRefresh("tenant-b", [otherTenant], null);

        var failed = store.ApplyRefresh("tenant-a", null, new HttpRequestException("offline"));

        Assert.True(failed.KeptPreviousData);
        Assert.Single(failed.Snapshot.Assignments);
        Assert.Equal("offline", failed.Snapshot.Error);
        Assert.Equal(retainedAt, failed.Snapshot.RetrievedAt);
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
    public void DiscoversExistingEdgeProfilesFromLocalState()
    {
        var userDataDirectory = Path.Combine(Path.GetTempPath(), $"pimtoolbox-edge-discovery-{Guid.NewGuid():N}");
        var defaultProfile = Path.Combine(userDataDirectory, "Default");
        var workProfile = Path.Combine(userDataDirectory, "Profile 1");
        Directory.CreateDirectory(defaultProfile);
        Directory.CreateDirectory(workProfile);
        try
        {
            File.WriteAllText(Path.Combine(userDataDirectory, "Local State"), """
                {
                  "profile": {
                    "info_cache": {
                      "Default": { "name": "Personal", "user_name": "personal@example.com" },
                      "Profile 1": { "name": "Work", "user_name": "admin@example.com" }
                    }
                  }
                }
                """);

            var profiles = EdgeProfileDiscovery.DiscoverFromUserDataDirectory(
                userDataDirectory, "C:\\Edge\\msedge.exe");

            Assert.Equal(2, profiles.Count);
            var work = Assert.Single(profiles, profile => profile.ProfileDirectory == "Profile 1");
            Assert.Equal("Work — admin@example.com (Profile 1)", work.DisplayName);
            Assert.Equal(Path.GetFullPath(userDataDirectory), work.UserDataDirectory);
        }
        finally
        {
            Directory.Delete(userDataDirectory, recursive: true);
        }
    }


    [Fact]
    public void SelectingDetectedEdgeProfileUpdatesTenantConfiguration()
    {
        var tenant = new TenantEditorModel("Work");
        var profile = new EdgeProfileOption(
            "Work — admin@example.com (Profile 1)",
            @"C:\Edge\msedge.exe",
            @"C:\Users\user\Microsoft\Edge\User Data",
            "Profile 1");
        tenant.SetEdgeProfiles([EdgeProfileOption.SystemBrowser, profile]);
        tenant.SelectedEdgeProfile = profile;

        Assert.Equal(profile.EdgeExecutable, tenant.EdgeExecutable);
        Assert.Equal(profile.UserDataDirectory, tenant.EdgeUserDataDirectory);
        Assert.Equal(profile.ProfileDirectory, tenant.EdgeProfileDirectory);
        Assert.Same(profile, tenant.SelectedEdgeProfile);
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
    public async Task SettingsSaveRejectsSharedEdgeProfileDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pimtoolbox-settings-test-{Guid.NewGuid():N}");
        var sharedProfile = Path.Combine(directory, "shared-edge-profile");
        var settings = new PimToolboxSettings
        {
            Tenants =
            [
                new TenantConfiguration("Alpha", "tenant-a", "client-a", "edge.exe", sharedProfile),
                new TenantConfiguration("Beta", "tenant-b", "client-b", "edge.exe", sharedProfile)
            ]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PimToolboxSettingsStore(directory).SaveAsync(settings));
        Assert.False(File.Exists(Path.Combine(directory, "settings.v2.json")));
    }


    [Fact]
    public async Task SettingsSaveAllowsDifferentProfilesInSharedEdgeUserDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pimtoolbox-settings-test-{Guid.NewGuid():N}");
        var sharedUserData = Path.Combine(directory, "edge-user-data");
        var settings = new PimToolboxSettings
        {
            Tenants =
            [
                new TenantConfiguration("Alpha", "tenant-a", "client-a", "edge.exe", sharedUserData, "Default"),
                new TenantConfiguration("Beta", "tenant-b", "client-b", "edge.exe", sharedUserData, "Profile 1")
            ]
        };

        try
        {
            var store = new PimToolboxSettingsStore(directory);
            await store.SaveAsync(settings);
            var saved = await store.LoadAsync();
            Assert.Equal(["Default", "Profile 1"], saved.Tenants.Select(tenant => tenant.EdgeProfileDirectory));
        }
        finally
        {
            if (Directory.Exists(directory))
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


public sealed class HeadlessTestApplication : Application
{
    public override void Initialize()
    {
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

public class DesktopAccessibilityTests
{
    [AvaloniaFact]
    public void MainWindowShowsPointerAndSelectionHighlights()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var primaryButton = window.FindControl<Button>("RefreshAllButton");
            var ordinaryButton = window.FindControl<Button>("SettingsButton");
            var assignmentsList = window.FindControl<ListBox>("AssignmentsList");
            Assert.NotNull(primaryButton);
            Assert.NotNull(ordinaryButton);
            Assert.NotNull(assignmentsList);

            var primaryPresenter = primaryButton!.GetVisualDescendants().OfType<ContentPresenter>().Single();
            var initialPrimaryBackground = primaryPresenter.Background;
            var primaryPoint = primaryButton.TranslatePoint(primaryButton.Bounds.Center, window);
            Assert.NotNull(primaryPoint);
            window.MouseMove(primaryPoint!.Value);
            window.UpdateLayout();
            Assert.Contains(":pointerover", primaryButton.Classes);
            Assert.NotEqual(initialPrimaryBackground, primaryPresenter.Background);

            var ordinaryPresenter = ordinaryButton!.GetVisualDescendants().OfType<ContentPresenter>().Single();
            var initialOrdinaryBackground = ordinaryPresenter.Background;
            ((IPseudoClasses)ordinaryButton.Classes).Add(":pointerover");
            window.UpdateLayout();
            Assert.NotEqual(initialOrdinaryBackground, ordinaryPresenter.Background);

            var assignmentCard = new Border
            {
                Classes = { "AssignmentCard" }
            };
            var dashboard = window.FindControl<Grid>("DashboardLayout");
            Assert.NotNull(dashboard);
            var item = new ListBoxItem { Content = assignmentCard };
            Grid.SetRow(item, 0);
            Grid.SetColumn(item, 1);
            dashboard!.Children.Add(item);
            window.UpdateLayout();

            var initialCardBackground = assignmentCard.Background;
            ((IPseudoClasses)item.Classes).Add(":pointerover");
            window.UpdateLayout();
            Assert.NotEqual(initialCardBackground, assignmentCard.Background);

            ((IPseudoClasses)item.Classes).Remove(":pointerover");
            item.IsSelected = true;
            window.UpdateLayout();
            Assert.Equal(Avalonia.Media.Color.Parse("#1C2B4A"),
                (assignmentCard.Background as Avalonia.Media.ISolidColorBrush)?.Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindowMovesInspectorBelowAssignmentsAtCompactWidths()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var dashboard = window.FindControl<Grid>("DashboardLayout");
            var inspector = window.FindControl<Border>("InspectorPanel");
            Assert.NotNull(dashboard);
            Assert.NotNull(inspector);

            dashboard.RaiseEvent(new SizeChangedEventArgs(Control.SizeChangedEvent, dashboard,
                new Size(1200, 800), new Size(900, 800)));
            Assert.Equal(1, Grid.GetColumn(inspector));
            Assert.Equal(1, Grid.GetRow(inspector));
            Assert.Equal(1, Grid.GetColumnSpan(inspector));

            dashboard.RaiseEvent(new SizeChangedEventArgs(Control.SizeChangedEvent, dashboard,
                new Size(900, 800), new Size(1200, 800)));
            Assert.Equal(2, Grid.GetColumn(inspector));
            Assert.Equal(0, Grid.GetRow(inspector));
            Assert.Equal(1, Grid.GetColumnSpan(inspector));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindowShowsSetupEmptyStateWhenNoTenantsAreConfigured()
    {
        var window = new MainWindow();
        try
        {
            var searchBox = window.FindControl<TextBox>("SearchBox");
            var emptyPanel = window.FindControl<Border>("EmptyAssignmentsPanel");
            var emptyTitle = window.FindControl<TextBlock>("EmptyAssignmentsTitle");
            var emptyMessage = window.FindControl<TextBlock>("EmptyAssignmentsMessage");
            var emptyAction = window.FindControl<Button>("EmptyAssignmentsActionButton");

            Assert.NotNull(searchBox);
            Assert.NotNull(emptyPanel);
            Assert.NotNull(emptyTitle);
            Assert.NotNull(emptyMessage);
            Assert.NotNull(emptyAction);

            searchBox!.Text = "no match";
            Assert.True(emptyPanel!.IsVisible);
            Assert.False(window.FindControl<ListBox>("AssignmentsList")!.IsVisible);
            Assert.Equal("No tenant profiles yet", emptyTitle!.Text);
            Assert.Contains("Add a tenant profile", emptyMessage!.Text);
            Assert.Equal("Open settings", emptyAction!.Content);
            Assert.Equal("AssignmentEmptyState", AutomationProperties.GetAutomationId(emptyPanel));
            Assert.Equal("AssignmentEmptyStateAction", AutomationProperties.GetAutomationId(emptyAction));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindowDistinguishesUnloadedEmptyAndFilteredAssignmentStates()
    {
        var window = new MainWindow();
        try
        {
            var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
            var snapshotStoreField = typeof(MainWindow).GetField("_snapshotStore", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(settingsField);
            Assert.NotNull(snapshotStoreField);

            var tenant = new TenantConfiguration("Alpha", "tenant-a", "client-a", "", "", "Default", "");
            settingsField!.SetValue(window, new PimToolboxSettings { Tenants = [tenant] });
            Assert.Single(((PimToolboxSettings)settingsField.GetValue(window)!).Tenants);
            var snapshots = (TenantSnapshotStore)snapshotStoreField!.GetValue(window)!;
            var refreshAssignments = typeof(MainWindow).GetMethod("RefreshAssignmentList", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(refreshAssignments);
            var searchBox = window.FindControl<TextBox>("SearchBox");
            var emptyPanel = window.FindControl<Border>("EmptyAssignmentsPanel");
            var emptyTitle = window.FindControl<TextBlock>("EmptyAssignmentsTitle");
            var actionButton = window.FindControl<Button>("EmptyAssignmentsActionButton");
            Assert.NotNull(searchBox);
            Assert.NotNull(emptyPanel);
            Assert.NotNull(emptyTitle);
            Assert.NotNull(actionButton);

            searchBox!.Text = "trigger empty state";
            refreshAssignments!.Invoke(window, null);
            Assert.True(emptyPanel!.IsVisible);
            Assert.Equal("No assignments loaded yet", emptyTitle!.Text);
            Assert.Equal("Refresh all", actionButton!.Content);
            Assert.Equal("Refresh all tenants", AutomationProperties.GetName(actionButton));

            snapshots.ApplyRefresh(tenant.TenantId, [], null);
            searchBox.Clear();
            refreshAssignments.Invoke(window, null);
            Assert.True(emptyPanel.IsVisible);
            Assert.Equal("No assignments found", emptyTitle.Text);

            snapshots.ApplyRefresh(tenant.TenantId,
                [new PimAssignment(tenant.TenantId, tenant.Name, AssignmentFamily.DirectoryRole, "role-a", "/",
                    "user-a", "Reader", AssignmentStatus.Eligible)], null);
            searchBox.Text = "does not match";
            refreshAssignments.Invoke(window, null);
            Assert.True(emptyPanel.IsVisible);
            Assert.Equal("No assignments match these filters", emptyTitle.Text);
            Assert.Equal("Clear filters", actionButton.Content);
            Assert.Equal("Clear assignment filters", AutomationProperties.GetName(actionButton));

            actionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(emptyPanel.IsVisible);
            Assert.True(window.FindControl<ListBox>("AssignmentsList")!.IsVisible);
            Assert.Equal(string.Empty, searchBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowExposesAccessibleNamesAndLabelRelationships()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var settingsButton = window.FindControl<Button>("SettingsButton");
            var signInBusyIndicator = window.FindControl<ProgressBar>("SignInBusyIndicator");
            var searchBox = window.FindControl<TextBox>("SearchBox");
            var statusFilter = window.FindControl<ComboBox>("StatusFilter");
            var durationBox = window.FindControl<TextBox>("DurationBox");
            var justificationBox = window.FindControl<TextBox>("JustificationBox");
            var searchLabel = window.FindControl<TextBlock>("SearchLabel");
            var statusLabel = window.FindControl<TextBlock>("StatusLabel");
            var durationLabel = window.FindControl<TextBlock>("DurationLabel");
            var justificationLabel = window.FindControl<TextBlock>("JustificationLabel");
            var refreshPanel = window.FindControl<Border>("RefreshProgressPanel");
            var refreshProgress = window.FindControl<TextBlock>("RefreshProgressText");
            var refreshProgressBar = window.FindControl<ProgressBar>("RefreshProgressBar");
            var cancelButton = window.FindControl<Button>("CancelButton");
            var errorBanner = window.FindControl<Border>("ErrorBanner");
            var errorBannerText = window.FindControl<TextBlock>("ErrorBannerText");
            var dismissErrorButton = window.FindControl<Button>("DismissErrorButton");
            var confirmationOverlay = window.FindControl<Grid>("ConfirmationOverlay");
            var confirmationTitle = window.FindControl<TextBlock>("ConfirmationTitle");
            var confirmationDetails = window.FindControl<TextBlock>("ConfirmationDetails");
            var confirmationConfirmButton = window.FindControl<Button>("ConfirmationConfirmButton");
            var confirmationCancelButton = window.FindControl<Button>("ConfirmationCancelButton");

            Assert.NotNull(settingsButton);
            var buttonBrushTransitions = settingsButton!.Transitions!.OfType<BrushTransition>().ToArray();
            Assert.Equal(2, buttonBrushTransitions.Length);
            Assert.All(buttonBrushTransitions, transition =>
                Assert.Equal(TimeSpan.FromMilliseconds(120), transition.Duration));
            Assert.NotNull(signInBusyIndicator);
            Assert.NotNull(searchBox);
            Assert.NotNull(statusFilter);
            Assert.NotNull(durationBox);
            Assert.NotNull(justificationBox);
            Assert.NotNull(searchLabel);
            Assert.NotNull(statusLabel);
            Assert.NotNull(durationLabel);
            Assert.NotNull(justificationLabel);
            Assert.NotNull(refreshPanel);
            Assert.NotNull(refreshProgress);
            Assert.NotNull(refreshProgressBar);
            Assert.NotNull(cancelButton);
            Assert.NotNull(errorBanner);
            Assert.NotNull(errorBannerText);
            Assert.NotNull(dismissErrorButton);
            Assert.NotNull(confirmationOverlay);
            Assert.NotNull(confirmationTitle);
            Assert.NotNull(confirmationDetails);
            Assert.NotNull(confirmationConfirmButton);
            Assert.NotNull(confirmationCancelButton);
            Assert.False(signInBusyIndicator.IsVisible);
            Assert.True(signInBusyIndicator.IsIndeterminate);
            Assert.Equal("Sign-in in progress", AutomationProperties.GetName(signInBusyIndicator));
            Assert.Equal("SignInBusyIndicator", AutomationProperties.GetAutomationId(signInBusyIndicator));
            Assert.False(refreshPanel.IsVisible);
            Assert.True(refreshProgressBar.IsIndeterminate);
            Assert.False(cancelButton.IsEnabled);
            Assert.False(errorBanner.IsVisible);
            Assert.Equal("Application error", AutomationProperties.GetName(errorBanner));
            Assert.Equal("ErrorBanner", AutomationProperties.GetAutomationId(errorBanner));
            Assert.Equal("Dismiss error message", AutomationProperties.GetName(dismissErrorButton));
            Assert.Equal("DismissErrorButton", AutomationProperties.GetAutomationId(dismissErrorButton));
            Assert.Equal("Open settings", AutomationProperties.GetName(settingsButton));
            Assert.Equal("SettingsButton", AutomationProperties.GetAutomationId(settingsButton));
            Assert.Equal("Search assignments", AutomationProperties.GetName(searchBox));
            Assert.Equal("AssignmentSearch", AutomationProperties.GetAutomationId(searchBox));
            Assert.Same(searchLabel, AutomationProperties.GetLabeledBy(searchBox));
            Assert.Same(statusLabel, AutomationProperties.GetLabeledBy(statusFilter));
            Assert.Equal("Activation duration", AutomationProperties.GetName(durationBox));
            Assert.Equal("ActivationDuration", AutomationProperties.GetAutomationId(durationBox));
            Assert.Same(durationLabel, AutomationProperties.GetLabeledBy(durationBox));
            Assert.Equal("Justification", AutomationProperties.GetName(justificationBox));
            Assert.Equal("ActivationJustification", AutomationProperties.GetAutomationId(justificationBox));
            Assert.Same(justificationLabel, AutomationProperties.GetLabeledBy(justificationBox));
            Assert.Equal("Refresh progress", AutomationProperties.GetName(refreshProgress));
            Assert.Equal("RefreshProgressText", AutomationProperties.GetAutomationId(refreshProgress));
            Assert.Equal("Cancel tenant refresh", AutomationProperties.GetName(cancelButton));
            Assert.Equal("CancelButton", AutomationProperties.GetAutomationId(cancelButton));
            Assert.False(confirmationOverlay.IsVisible);
            Assert.Equal("Assignment action confirmation", AutomationProperties.GetName(confirmationOverlay));
            Assert.Equal("AssignmentConfirmationDialog", AutomationProperties.GetAutomationId(confirmationOverlay));
            Assert.Equal("Confirm assignment action", AutomationProperties.GetName(confirmationConfirmButton));
            Assert.Equal("Cancel assignment action", AutomationProperties.GetName(confirmationCancelButton));

            var confirmAsync = typeof(MainWindow).GetMethod("ConfirmAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingConfirmation = (Task<bool>)confirmAsync!.Invoke(window,
                ["Confirm activation", "Admin role\nTenant: Alpha"])!;
            var mainContent = window.FindControl<Grid>("MainContent");
            Assert.True(confirmationOverlay.IsVisible);
            Assert.False(mainContent!.IsEnabled);
            Assert.Equal("Confirm activation", confirmationTitle.Text);
            Assert.Equal("Admin role\nTenant: Alpha", confirmationDetails.Text);
            confirmationCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(await pendingConfirmation);
            Assert.False(confirmationOverlay.IsVisible);
            Assert.True(mainContent.IsEnabled);
            Assert.True(searchBox.IsFocused);

            pendingConfirmation = (Task<bool>)confirmAsync.Invoke(window,
                ["Confirm deactivation", "Admin role\nTenant: Alpha"])!;
            confirmationConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await pendingConfirmation);
            Assert.False(confirmationOverlay.IsVisible);
            Assert.Equal(0, Grid.GetRow(cancelButton));
            Assert.Equal(1, Grid.GetColumn(cancelButton));
            var refreshProgressTrack = Assert.IsType<Border>(refreshProgressBar.Parent);
            Assert.Equal(1, Grid.GetRow(refreshProgressTrack));
            Assert.Equal(2, Grid.GetColumnSpan(refreshProgressTrack));
            Assert.Equal(8, refreshProgressBar.Height);
            Assert.Equal(8, refreshProgressTrack.Height);
            Assert.Contains("refresh-progress", refreshProgressBar.Classes);

            errorBannerText.Text = "Refresh failed for Alpha.";
            errorBanner.IsVisible = true;
            window.FindControl<TextBlock>("FooterText")!.Text = "Refresh complete.";
            Assert.True(errorBanner.IsVisible);
            dismissErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(errorBanner.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }


    [AvaloniaFact]
    public void TabKeyMovesFromSearchThroughStatusToActivationFields()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();

            var searchBox = window.FindControl<TextBox>("SearchBox");
            var statusFilter = window.FindControl<ComboBox>("StatusFilter");
            var durationBox = window.FindControl<TextBox>("DurationBox");
            var justificationBox = window.FindControl<TextBox>("JustificationBox");

            Assert.NotNull(searchBox);
            Assert.NotNull(statusFilter);
            Assert.NotNull(durationBox);
            Assert.NotNull(justificationBox);
            Assert.True(searchBox.Focus());

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(statusFilter, window.FocusManager?.GetFocusedElement());

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(durationBox, window.FocusManager?.GetFocusedElement());

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Same(justificationBox, window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }


    [AvaloniaFact]
    public void CtrlF_FocusesSearchAndSelectsExistingText()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var searchBox = window.FindControl<TextBox>("SearchBox");
            Assert.NotNull(searchBox);
            searchBox.Text = "Reader";

            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.F, RawInputModifiers.Control);

            Assert.Same(searchBox, window.FocusManager?.GetFocusedElement());
            Assert.Equal("Reader", searchBox.SelectedText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeClearsSearchWhenSearchHasFocus()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var searchBox = window.FindControl<TextBox>("SearchBox");
            Assert.NotNull(searchBox);
            searchBox.Text = "Reader";
            Assert.True(searchBox.Focus());

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            Assert.Equal(string.Empty, searchBox.Text);
            Assert.Same(searchBox, window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }


    [AvaloniaFact]
    public void SettingsViewStacksPairedFieldsAtCompactWidths()
    {
        var view = new SettingsView();
        var host = new Window { Content = view, Width = 860, Height = 700 };
        try
        {
            host.Show();
            host.UpdateLayout();

            var workdayFields = view.FindControl<Grid>("WorkdayFields");
            var workdayEndField = view.FindControl<StackPanel>("WorkdayEndField");
            Assert.NotNull(workdayFields);
            Assert.NotNull(workdayEndField);
            Assert.Equal(0, Grid.GetRow(workdayEndField));
            Assert.Equal(1, Grid.GetColumn(workdayEndField));

            workdayFields.RaiseEvent(new SizeChangedEventArgs(Control.SizeChangedEvent, workdayFields,
                new Size(860, 100), new Size(650, 100)));
            Assert.Equal(1, Grid.GetRow(workdayEndField));
            Assert.Equal(0, Grid.GetColumn(workdayEndField));

            workdayFields.RaiseEvent(new SizeChangedEventArgs(Control.SizeChangedEvent, workdayFields,
                new Size(650, 100), new Size(860, 100)));
            Assert.Equal(0, Grid.GetRow(workdayEndField));
            Assert.Equal(1, Grid.GetColumn(workdayEndField));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsViewLoadsAndSavesJsonWhilePreservingAppManagedSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pimtoolbox-settings-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new PimToolboxSettingsStore(directory);
        var activationTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialSettings = new PimToolboxSettings
        {
            Tenants = [new TenantConfiguration("Alpha", "tenant-a", "client-a", "", "", "Default", "admin@alpha.example")],
            FavoriteKeys = ["favorite-key"],
            GlobalJustification = "Initial justification",
            WorkStartTime = "08:00",
            WorkEndTime = "17:00",
            CloseAction = "MinimizeToTray",
            ActivationStartTimes = new Dictionary<string, DateTimeOffset>
            {
                ["activation-key"] = activationTime
            }
        };
        Window? host = null;

        try
        {
            await store.SaveAsync(initialSettings);
            var settingsLoadedFromJson = await store.LoadAsync();
            var view = new SettingsView(settingsLoadedFromJson, store);
            var saved = new TaskCompletionSource<PimToolboxSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.SettingsSaved += settings => saved.TrySetResult(settings);
            host = new Window { Content = view, Width = 860, Height = 820 };
            host.Show();
            host.UpdateLayout();

            var workdayEndField = view.FindControl<StackPanel>("WorkdayEndField");
            Assert.NotNull(workdayEndField);
            Assert.Equal(0, Grid.GetRow(workdayEndField));
            Assert.Equal(1, Grid.GetColumn(workdayEndField));

            var justificationBox = view.FindControl<TextBox>("GlobalJustificationBox");
            var startTimeBox = view.FindControl<TextBox>("WorkStartTimeBox");
            var endTimeBox = view.FindControl<TextBox>("WorkEndTimeBox");
            var addTenantButton = view.FindControl<Button>("AddTenantButton");
            var closeBehavior = view.FindControl<ComboBox>("CloseBehavior");
            var saveButton = view.FindControl<Button>("SaveButton");
            Assert.NotNull(justificationBox);
            Assert.NotNull(startTimeBox);
            Assert.NotNull(endTimeBox);
            Assert.NotNull(addTenantButton);
            Assert.NotNull(closeBehavior);
            Assert.NotNull(saveButton);
            Assert.Equal("Initial justification", justificationBox.Text);
            Assert.Equal("08:00", startTimeBox.Text);
            Assert.Equal("17:00", endTimeBox.Text);
            Assert.Equal(2, view.CloseActionIndex);
            Assert.Equal(2, closeBehavior.SelectedIndex);
            Assert.Single(view.Tenants);
            Assert.Equal("Alpha", view.Tenants[0].Name);
            addTenantButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, view.Tenants.Count);
            view.Tenants.RemoveAt(1);

            view.GlobalJustification = "Updated justification";
            view.WorkStartTime = "09:15";
            closeBehavior.SelectedIndex = 1;
            Assert.Equal(1, view.CloseActionIndex);
            view.Tenants[0].Name = "Alpha Updated";
            view.Tenants.Add(new TenantEditorModel("Beta", "tenant-b", "client-b"));
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var savedSettings = await store.LoadAsync();
            Assert.Equal("Updated justification", savedSettings.GlobalJustification);
            Assert.Equal("09:15", savedSettings.WorkStartTime);
            Assert.Equal("17:00", savedSettings.WorkEndTime);
            Assert.Equal("Exit", savedSettings.CloseAction);
            Assert.Equal(["Alpha Updated", "Beta"], savedSettings.Tenants.Select(tenant => tenant.Name));
            Assert.Equal("admin@alpha.example", savedSettings.Tenants[0].ExpectedUsername);
            Assert.Contains("favorite-key", savedSettings.FavoriteKeys);
            Assert.Equal(activationTime, savedSettings.ActivationStartTimes["activation-key"]);
        }
        finally
        {
            host?.Close();
            Directory.Delete(directory, recursive: true);
        }
    }
}
