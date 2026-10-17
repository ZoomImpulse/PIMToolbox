using System.Collections.Concurrent;
using System.Xml;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PIMToolbox.Core;

namespace PIMToolbox.Desktop;

public partial class MainWindow : Window
{
    private readonly PimToolboxSettingsStore _settingsStore = new();
    private TenantSnapshotStore _snapshotStore = new();
    private bool _settingsLoaded;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, AuthenticationProof> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assignmentOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantRefreshGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private readonly HashSet<string> _notified = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private PimToolboxSettings _settings = new();
    private GraphPimClient _graph = null!;
    private TenantActionRouter _actionRouter = null!;
    private PimAssignment? _selectedAssignment;
    private bool _isBusy;
    private bool _isUpdatingNavigation;
    private string? _tenantFilter;
    private EmptyStateAction _emptyStateAction;
    private CancellationTokenSource? _operationCancellation;
    private static readonly string[] TenantAccents = ["#0093E5", "#8E8BE8", "#55B7F3", "#6E6BCB", "#7C9FF2"];

    private const string ExitCloseAction = "Exit";
    private const string MinimizeCloseAction = "MinimizeToTray";
    private string? _closeActionForSession;
    private bool _settingsLoadFailed;

    public MainWindow()
    {
        InitializeComponent();
        ApplyResponsiveLayout(Width);
        SettingsEditor.SettingsSaved += OnSettingsSaved;
        SettingsEditor.CancelRequested += OnSettingsCancelRequested;
        _graph = new GraphPimClient(_httpClient);
        StatusFilter.SelectedIndex = 0;
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
        Opened += OnOpened;
        Closing += OnClosing;
        AssignmentsList.SelectionChanged += OnSelectionChanged;
        JustificationBox.Text = "PIM Quick Action";
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            if (_closeActionForSession is not null)
            {
                _settings.CloseAction = _closeActionForSession;
                await SaveSettingsAsync();
            }

            _settingsLoaded = true;
            UpdateClosePromptAvailability();
            JustificationBox.Text = _settings.GlobalJustification;
            RefreshTenantNavigation();
            _actionRouter = new TenantActionRouter(_settings.Tenants, GetTokenAsync, _graph);
            FooterText.Text = _settings.Tenants.Count == 0
                ? $"Add tenant profiles in Settings ({_settingsStore.SettingsPath}) to get started."
                : $"Settings: {_settingsStore.SettingsPath}";
            UpdateTenantStatus();
            RefreshAssignmentList();
        }
        catch (Exception error)
        {
            _settingsLoadFailed = true;
            UpdateClosePromptAvailability($"Settings could not be loaded or saved: {error.Message}");
            FooterText.Text = $"Settings could not be loaded: {error.Message}";
            ShowErrorBanner(FooterText.Text);
        }
    }

    private async void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _assignmentOperations.Count > 0)
        {
            FooterText.Text = "Wait for all current tenant actions to finish before signing in.";
            return;
        }
        var tenant = _settings.Tenants.FirstOrDefault(item =>
            _tenantFilter is null || item.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase));
        if (tenant is null)
        {
            FooterText.Text = "Select a tenant workspace or configure a tenant first.";
            return;
        }

        SetBusyState(true);
        SignInBusyIndicator.IsVisible = true;
        SignInButtonLabel.Text = "Signing in…";
        FooterText.Text = $"Signing in to {tenant.Name}…";
        try
        {
            await SignInAsync(tenant);
        }
        catch (Exception error)
        {
            FooterText.Text = $"Sign-in failed for {tenant.Name}: {error.Message}";
            ShowErrorBanner(FooterText.Text);
        }
        finally
        {
            SetBusyState(false);
            SignInBusyIndicator.IsVisible = false;
            SignInButtonLabel.Text = "Sign in / verify";
            UpdateInspector();
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _assignmentOperations.Count > 0) return;
        if (_settings.Tenants.Count == 0)
        {
            FooterText.Text = "Configure at least one tenant before refreshing.";
            return;
        }

        SetBusyState(true);
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        var succeeded = new List<string>();
        var failed = new List<string>();
        var tenantCount = _settings.Tenants.Count;
        RefreshProgressPanel.IsVisible = true;
        CancelButton.IsEnabled = true;
        try
        {
            for (var index = 0; index < tenantCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tenant = _settings.Tenants[index];
                RefreshProgressText.Text = $"Refreshing tenant {index + 1} of {tenantCount}: {tenant.Name}";
                FooterText.Text = RefreshProgressText.Text;
                try
                {
                    var session = await SignInAsync(tenant, cancellationToken);
                    var assignments = await _graph.RetrieveAsync(tenant, session.AccessToken, cancellationToken);
                    _snapshotStore.ApplyRefresh(tenant.TenantId, ApplyKnownActivationTimes(assignments), null);
                    succeeded.Add(tenant.Name);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    _snapshotStore.ApplyRefresh(tenant.TenantId, null, error);
                    failed.Add(tenant.Name);
                }
                UpdateTenantStatus();
                RefreshAssignmentList();
            }

            if (failed.Count == 0)
            {
                FooterText.Text = $"Refresh finished: all {succeeded.Count} tenant(s) refreshed successfully.";
            }
            else
            {
                FooterText.Text = $"Refresh finished: {succeeded.Count} succeeded; {failed.Count} failed ({string.Join(", ", failed)}). Failed tenant data is marked stale where available.";
                ShowErrorBanner(FooterText.Text);
            }
        }
        catch (OperationCanceledException)
        {
            var completedCount = succeeded.Count + failed.Count;
            var failureSummary = failed.Count == 0 ? string.Empty : $"; failed: {string.Join(", ", failed)}";
            FooterText.Text = $"Refresh cancelled after {completedCount} of {tenantCount} tenants ({succeeded.Count} succeeded{failureSummary}); remaining tenants were not changed.";
            if (failed.Count > 0)
                ShowErrorBanner($"Refresh cancelled with failed tenants: {string.Join(", ", failed)}.");
        }
        finally
        {
            SetBusyState(false);
            RefreshProgressPanel.IsVisible = false;
            CancelButton.IsEnabled = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            UpdateTenantStatus();
            UpdateInspector();
        }
    }

    private async Task<AuthenticationProof> SignInAsync(
        TenantConfiguration tenant,
        CancellationToken cancellationToken = default)
    {
        var auth = new TenantAuthenticationService(_httpClient);
        var proof = await auth.SignInAndVerifyAsync(tenant, cancellationToken);
        _sessions[tenant.TenantId] = proof;
        FooterText.Text = $"Verified {proof.Identity.Username} in tenant {tenant.Name}; Graph /me read succeeded.";
        UpdateTenantStatus();
        return proof;
    }

    private async Task<string> GetTokenAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        var session = await SignInAsync(tenant, cancellationToken);
        return session.AccessToken;
    }

    private async void OnActivateClick(object? sender, RoutedEventArgs e)
    {
        var assignment = _selectedAssignment;
        if (assignment is null || _actionRouter is null || _isBusy) return;
        var operationKey = GetAssignmentOperationKey(assignment);
        if (!_assignmentOperations.TryAdd(operationKey, "Preparing activation…")) return;
        UpdateAssignmentOperations();
        FooterText.Text = $"Preparing activation for {assignment.DisplayName}…";
        try
        {
            var policyMaximum = await _actionRouter.GetMaximumDurationAsync(assignment);
            var maximum = policyMaximum ?? TimeSpan.FromHours(8);
            var suggested = DurationCalculator.Suggest(maximum,
                TimeOnly.Parse(_settings.WorkStartTime),
                TimeOnly.Parse(_settings.WorkEndTime),
                DateTimeOffset.Now);
            var duration = string.IsNullOrWhiteSpace(DurationBox.Text)
                ? suggested
                : XmlConvert.ToTimeSpan(DurationBox.Text);
            if (duration > maximum)
                throw new InvalidOperationException($"The requested duration exceeds this assignment's maximum of {XmlConvert.ToString(maximum)}.");
            var justification = string.IsNullOrWhiteSpace(JustificationBox.Text)
                ? _settings.GlobalJustification
                : JustificationBox.Text;
            if (!await ConfirmAsync("Confirm activation",
                    $"{assignment.DisplayName}\nTenant: {assignment.TenantName}\nDuration: {XmlConvert.ToString(duration)}\nJustification: {justification}"))
            {
                FooterText.Text = "Activation cancelled.";
                return;
            }

            _assignmentOperations[operationKey] = "Activating…";
            UpdateAssignmentOperations();
            FooterText.Text = $"Activating {assignment.DisplayName}…";
            _settings.GlobalJustification = justification;
            await SaveSettingsAsync();
            var activationStarted = DateTimeOffset.UtcNow;
            var result = await _actionRouter.ActivateAsync(assignment, duration, justification);
            var actionMessage = ActionResultMessage("Activation", assignment, result, duration);
            if (!result.PendingApproval)
            {
                foreach (var key in PimToolboxSettingsStore.ActivationKeys(assignment))
                    _settings.ActivationStartTimes[key] = activationStarted;
                try { await SaveSettingsAsync(); }
                catch (Exception error)
                {
                    actionMessage += $" Local activation time could not be saved: {error.Message}";
                    ShowErrorBanner($"Activation completed, but its local start time could not be saved: {error.Message}");
                }
            }
            FooterText.Text = actionMessage;
            await RefreshTenantAsync(_settings.Tenants.Single(item =>
                item.TenantId.Equals(assignment.TenantId, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error)
        {
            FooterText.Text = $"Activation failed: {error.Message}";
            ShowErrorBanner(FooterText.Text);
        }
        finally
        {
            _assignmentOperations.Remove(operationKey);
            UpdateAssignmentOperations();
        }
    }

    private async void OnDeactivateClick(object? sender, RoutedEventArgs e)
    {
        var assignment = _selectedAssignment;
        if (assignment is null || _actionRouter is null || _isBusy) return;
        var operationKey = GetAssignmentOperationKey(assignment);
        if (!_assignmentOperations.TryAdd(operationKey, "Preparing deactivation…")) return;
        UpdateAssignmentOperations();
        FooterText.Text = $"Preparing deactivation for {assignment.DisplayName}…";
        try
        {
            if (!await ConfirmAsync("Confirm deactivation",
                    $"{assignment.DisplayName}\nTenant: {assignment.TenantName}"))
            {
                FooterText.Text = "Deactivation cancelled.";
                return;
            }

            _assignmentOperations[operationKey] = "Deactivating…";
            UpdateAssignmentOperations();
            FooterText.Text = $"Deactivating {assignment.DisplayName}…";
            var result = await _actionRouter.DeactivateAsync(assignment);
            var actionMessage = ActionResultMessage("Deactivation", assignment, result, null);
            if (result.Verified)
            {
                foreach (var key in PimToolboxSettingsStore.ActivationKeys(assignment))
                    _settings.ActivationStartTimes.Remove(key);
                try { await SaveSettingsAsync(); }
                catch (Exception error)
                {
                    actionMessage += $" Local activation time could not be cleared: {error.Message}";
                    ShowErrorBanner($"Deactivation completed, but its local start time could not be cleared: {error.Message}");
                }
            }
            FooterText.Text = actionMessage;
            await RefreshTenantAsync(_settings.Tenants.Single(item =>
                item.TenantId.Equals(assignment.TenantId, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error)
        {
            FooterText.Text = $"Deactivation failed: {error.Message}";
            ShowErrorBanner(FooterText.Text);
        }
        finally
        {
            _assignmentOperations.Remove(operationKey);
            UpdateAssignmentOperations();
        }
    }

    private async void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedAssignment is null) return;
        var key = PimToolboxSettingsStore.FavoriteKey(_selectedAssignment);
        if (!_settings.FavoriteKeys.Remove(key))
            _settings.FavoriteKeys.Add(key);
        try
        {
            _settings.GlobalJustification = JustificationBox.Text ?? "PIM Quick Action";
            await SaveSettingsAsync();
            RefreshAssignmentList();
            UpdateFavoriteButton();
        }
        catch (Exception error)
        {
            FooterText.Text = $"Favorite could not be saved: {error.Message}";
            ShowErrorBanner(FooterText.Text);
        }
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => RefreshAssignmentList();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ConfirmationOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                _confirmationCompletion?.TrySetResult(false);
                e.Handled = true;
            }

            return;
        }

        if (SettingsOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                CloseSettingsOverlay();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void OnSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) =>
        RefreshAssignmentList();

    private void OnStatusFilterChanged(object? sender, SelectionChangedEventArgs e) =>
        RefreshAssignmentList();

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null) return;
        CancelButton.IsEnabled = false;
        RefreshProgressText.Text = "Cancelling refresh…";
        FooterText.Text = "Cancelling tenant refresh…";
        _operationCancellation.Cancel();
    }


    private void OnDismissErrorBannerClick(object? sender, RoutedEventArgs e)
    {
        ErrorBanner.IsVisible = false;
    }

    private void ShowErrorBanner(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            FooterText.Text = "Settings are not ready yet. Please try again shortly.";
            return;
        }

        if (_isBusy || _assignmentOperations.Count > 0)
        {
            FooterText.Text = "Finish all current tenant actions before editing settings.";
            return;
        }

        SettingsEditor.LoadSettings(_settings, _settingsStore);
        MainContent.IsEnabled = false;
        SettingsOverlay.IsVisible = true;
        SettingsEditor.FocusFirstField();
    }

    private void OnSettingsCancelRequested(object? sender, EventArgs e) => CloseSettingsOverlay();

    private void OnSettingsSaved(PimToolboxSettings updatedSettings)
    {
        _settings = updatedSettings;
        _sessions.Clear();
        _snapshotStore = new TenantSnapshotStore();
        _selectedAssignment = null;
        _tenantFilter = null;
        _actionRouter = new TenantActionRouter(_settings.Tenants, GetTokenAsync, _graph);
        JustificationBox.Text = _settings.GlobalJustification;
        RefreshTenantNavigation();
        RefreshAssignmentList();
        CloseSettingsOverlay();
        FooterText.Text = "Settings saved and applied. Refresh tenants to load assignments with the updated profiles.";
    }

    private void CloseSettingsOverlay()
    {
        SettingsOverlay.IsVisible = false;
        MainContent.IsEnabled = true;
        SettingsButton.Focus();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedAssignment = (AssignmentsList.SelectedItem as AssignmentRow)?.Assignment;
        UpdateActionButtons();
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        var assignment = _selectedAssignment;
        var assignmentInProgress = assignment is not null &&
                                  _assignmentOperations.ContainsKey(GetAssignmentOperationKey(assignment));
        ActivateButton.IsEnabled = !_isBusy && !assignmentInProgress &&
                                   assignment?.Status == AssignmentStatus.Eligible;
        FavoriteButton.IsEnabled = !_isBusy && !assignmentInProgress && assignment is not null;
        if (assignment is null)
        {
            InspectorTitle.Text = "Select an assignment";
            InspectorTenant.Text = "—";
            InspectorType.Text = "—";
            InspectorSource.Text = "—";
            InspectorExpiry.Text = "—";
            InspectorStatusBadge.IsVisible = false;
            ActionSafetyText.Text = "Actions are routed using each assignment’s own tenant.";
            DeactivateButton.IsEnabled = false;
            return;
        }

        var remaining = DurationCalculator.GetRemaining(assignment.EndTime, DateTimeOffset.UtcNow);
        InspectorTitle.Text = assignment.DisplayName;
        InspectorTenant.Text = $"{assignment.TenantName}\n{assignment.TenantId}";
        InspectorType.Text = $"{assignment.Family} · {assignment.Status}";
        InspectorSource.Text = assignment.GroupSources is { Count: > 0 } groups
            ? string.Join(", ", groups.Select(group => group.GroupName))
            : assignment.IsGroupBacked ? "Group-backed source unresolved" : "Direct assignment";
        InspectorExpiry.Text = assignment.EndTime is null
            ? assignment.IsPermanent ? "Permanent assignment" : "No expiry"
            : remaining <= TimeSpan.Zero ? "Expired" : $"{GetExpiry(assignment)} remaining";
        InspectorStatus.Text = assignment.Status == AssignmentStatus.Active ? "ACTIVE" : "ELIGIBLE";
        InspectorStatus.Foreground = new SolidColorBrush(Color.Parse(
            assignment.Status == AssignmentStatus.Active ? "#55B7F3" : "#AAA7F5"));
        InspectorStatusBadge.Background = new SolidColorBrush(Color.Parse(
            assignment.Status == AssignmentStatus.Active ? "#102C40" : "#29274D"));
        InspectorStatusBadge.IsVisible = true;
        ActionSafetyText.Text = assignment.IsPermanent
            ? "Permanent assignments cannot be deactivated by this app."
            : assignment.Status == AssignmentStatus.Active &&
              assignment.StartTime is { } startTime &&
              DateTimeOffset.UtcNow - startTime < TimeSpan.FromMinutes(5)
                ? "Deactivation is locked for five minutes after activation."
                : $"Actions use the verified session for {assignment.TenantName}; group-backed roles target their source group.";
        UpdateActionButtons();
        UpdateFavoriteButton();
    }

    private void OnTenantNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingNavigation || TenantNavigation.SelectedItem is not TenantNavigationItem selected)
            return;
        _tenantFilter = selected.IsAll ? null : selected.TenantId;
        ViewTitle.Text = selected.IsAll ? "All assignments" : selected.Name;
        ViewSubtitle.Text = selected.IsAll
            ? "A live view across your configured tenants"
            : $"Assignments scoped to {selected.Name}";
        RefreshAssignmentList();
    }


    private void OnDashboardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var isCompact = width < 1120;
        DashboardLayout.ColumnDefinitions[0].Width = new GridLength(isCompact ? 190 : 218);
        DashboardLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        DashboardLayout.ColumnDefinitions[2].Width = new GridLength(isCompact ? 0 : 320);
        DashboardLayout.RowDefinitions[0].Height = new GridLength(isCompact ? 3 : 1, GridUnitType.Star);
        DashboardLayout.RowDefinitions[1].Height = new GridLength(isCompact ? 2 : 0, GridUnitType.Star);

        Grid.SetRowSpan(TenantSidebar, isCompact ? 2 : 1);
        Grid.SetColumn(InspectorPanel, isCompact ? 1 : 2);
        Grid.SetRow(InspectorPanel, isCompact ? 1 : 0);
        Grid.SetColumnSpan(InspectorPanel, 1);
    }

    private void RefreshTenantNavigation()
    {
        _isUpdatingNavigation = true;
        var entries = new List<TenantNavigationItem>
        {
            new(true, "", "All tenants", $"{_snapshotStore.GetMergedAssignments().Count} assignments",
                new SolidColorBrush(Color.Parse("#0093E5")), "P", new SolidColorBrush(Color.Parse("#91A0B4")), null)
        };
        for (var index = 0; index < _settings.Tenants.Count; index++)
        {
            var tenant = _settings.Tenants[index];
            var snapshot = _snapshotStore.Snapshots.FirstOrDefault(item =>
                item.TenantId.Equals(tenant.TenantId, StringComparison.OrdinalIgnoreCase));
            var detail = snapshot is null
                ? _sessions.ContainsKey(tenant.TenantId) ? "Signed in · not refreshed" : "Not connected"
                : snapshot.Error is not null
                    ? snapshot.RetrievedAt == DateTimeOffset.MinValue
                        ? "Refresh failed · no cached data"
                        : $"Stale data · {snapshot.Assignments.Count} cached"
                    : $"{snapshot.Assignments.Count} assignments · refreshed";
            var detailColor = snapshot?.Error is not null
                ? "#FFB86B"
                : _sessions.ContainsKey(tenant.TenantId) ? "#55B7F3" : "#8291A6";
            var accent = TenantAccents[index % TenantAccents.Length];
            var initials = new string(tenant.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2).Select(part => char.ToUpperInvariant(part[0])).ToArray());
            entries.Add(new TenantNavigationItem(false, tenant.TenantId, tenant.Name, detail,
                new SolidColorBrush(Color.Parse(accent)), initials, new SolidColorBrush(Color.Parse(detailColor)), snapshot?.Error));
        }
        TenantNavigation.ItemsSource = entries;
        var selectedIndex = _tenantFilter is null
            ? 0
            : entries.FindIndex(item => item.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase));
        TenantNavigation.SelectedIndex = Math.Max(0, selectedIndex);
        _isUpdatingNavigation = false;
        WorkspaceStatus.Text = _settings.Tenants.Count == 0
            ? "Add tenant profiles to get started."
            : $"{_sessions.Count} of {_settings.Tenants.Count} tenants verified";
    }

    private void UpdateFavoriteButton()
    {
        if (_selectedAssignment is null) return;
        var isFavorite = _settings.FavoriteKeys.Contains(
            PimToolboxSettingsStore.FavoriteKey(_selectedAssignment), StringComparer.OrdinalIgnoreCase);
        FavoriteButton.Content = isFavorite ? "★   Remove from favorites" : "☆   Add to favorites";
    }

    private void RefreshAssignmentList()
    {
        var allAssignments = _snapshotStore.GetMergedAssignments();
        var assignments = _tenantFilter is null
            ? allAssignments
            : allAssignments.Where(item =>
                item.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var hasFilters = _tenantFilter is not null || FavoritesOnly.IsChecked == true;
        if (FavoritesOnly.IsChecked == true)
            assignments = assignments.Where(item => _settings.FavoriteKeys.Contains(
                PimToolboxSettingsStore.FavoriteKey(item), StringComparer.OrdinalIgnoreCase)).ToArray();
        var search = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            hasFilters = true;
            assignments = assignments.Where(item =>
                item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.TenantName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Family.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.GroupSources?.Any(group => group.GroupName.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false))
                .ToArray();
        }
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (status is "Eligible" or "Active")
        {
            hasFilters = true;
            assignments = assignments.Where(item => item.Status.ToString() == status).ToArray();
        }

        var selected = _selectedAssignment;
        var rows = assignments.OrderBy(item => item.Status).ThenBy(item => item.DisplayName)
            .Select(item => new AssignmentRow(item, _assignmentOperations.GetValueOrDefault(GetAssignmentOperationKey(item)))).ToArray();
        AssignmentsList.ItemsSource = rows;
        AssignmentsList.SelectedItem = selected is null ? null : rows.FirstOrDefault(row =>
            row.Assignment.TenantId.Equals(selected.TenantId, StringComparison.OrdinalIgnoreCase) &&
            row.Assignment.Family == selected.Family &&
            row.Assignment.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase) &&
            row.Assignment.ResourceId.Equals(selected.ResourceId, StringComparison.OrdinalIgnoreCase));
        EligibleCount.Text = assignments.Count(item => item.Status == AssignmentStatus.Eligible).ToString();
        ActiveCount.Text = assignments.Count(item => item.Status == AssignmentStatus.Active).ToString();
        ExpiringCount.Text = assignments.Count(item => item.Status == AssignmentStatus.Active &&
            item.EndTime is { } end && end > DateTimeOffset.UtcNow && end <= DateTimeOffset.UtcNow.AddHours(1)).ToString();
        UpdateAssignmentEmptyState(allAssignments.Count, assignments.Count, hasFilters);
        UpdateInspector();
    }


    private void UpdateAssignmentEmptyState(int totalAssignmentCount, int filteredAssignmentCount, bool hasFilters)
    {
        var hasAssignments = filteredAssignmentCount > 0;
        AssignmentsList.IsVisible = hasAssignments;
        EmptyAssignmentsPanel.IsVisible = !hasAssignments;
        if (hasAssignments)
            return;

        if (_settings.Tenants.Count == 0)
        {
            EmptyAssignmentsTitle.Text = "No tenant profiles yet";
            EmptyAssignmentsMessage.Text = "Add a tenant profile to start loading and managing PIM assignments.";
            EmptyAssignmentsActionButton.Content = "Open settings";
            AutomationProperties.SetName(EmptyAssignmentsActionButton, "Open settings");
            _emptyStateAction = EmptyStateAction.ConfigureTenants;
            return;
        }

        if (hasFilters && totalAssignmentCount > 0)
        {
            EmptyAssignmentsTitle.Text = "No assignments match these filters";
            EmptyAssignmentsMessage.Text = "Try another search, status, tenant, or favorite filter.";
            EmptyAssignmentsActionButton.Content = "Clear filters";
            AutomationProperties.SetName(EmptyAssignmentsActionButton, "Clear assignment filters");
            _emptyStateAction = EmptyStateAction.ClearFilters;
            return;
        }

        IReadOnlyList<TenantConfiguration> tenantsInScope = _tenantFilter is null
            ? _settings.Tenants
            : _settings.Tenants.Where(tenant => tenant.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var allTenantsRefreshed = tenantsInScope.Count > 0 && tenantsInScope.All(tenant =>
        {
            var snapshot = _snapshotStore.Snapshots.FirstOrDefault(item =>
                item.TenantId.Equals(tenant.TenantId, StringComparison.OrdinalIgnoreCase));
            return snapshot is not null && snapshot.Error is null && snapshot.RetrievedAt != DateTimeOffset.MinValue;
        });
        EmptyAssignmentsTitle.Text = allTenantsRefreshed ? "No assignments found" : "No assignments loaded yet";
        EmptyAssignmentsMessage.Text = allTenantsRefreshed
            ? "Your tenant profiles returned no eligible or active assignments. Refresh again later to check for changes."
            : "Refresh your tenant profiles to see eligible and active assignments here.";
        EmptyAssignmentsActionButton.Content = "Refresh all";
        AutomationProperties.SetName(EmptyAssignmentsActionButton, "Refresh all tenants");
        _emptyStateAction = EmptyStateAction.RefreshAll;
    }

    private void OnEmptyAssignmentsActionClick(object? sender, RoutedEventArgs e)
    {
        switch (_emptyStateAction)
        {
            case EmptyStateAction.ConfigureTenants:
                OnOpenSettingsClick(sender, e);
                break;
            case EmptyStateAction.RefreshAll:
                OnRefreshClick(sender, e);
                break;
            case EmptyStateAction.ClearFilters:
                _tenantFilter = null;
                SearchBox.Clear();
                FavoritesOnly.IsChecked = false;
                StatusFilter.SelectedIndex = 0;
                RefreshTenantNavigation();
                RefreshAssignmentList();
                break;
        }
    }

    private static string GetExpiry(PimAssignment assignment)
    {
        if (assignment.EndTime is null) return assignment.IsPermanent ? "Permanent" : "No expiry";
        var remaining = DurationCalculator.GetRemaining(assignment.EndTime, DateTimeOffset.UtcNow);
        if (remaining <= TimeSpan.Zero) return "Expired";
        if (remaining < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)Math.Ceiling(remaining!.Value.TotalMinutes))}m";
        if (remaining < TimeSpan.FromDays(1)) return $"{(int)remaining!.Value.TotalHours}h {remaining.Value.Minutes}m";
        return $"{(int)remaining!.Value.TotalDays}d {remaining.Value.Hours}h";
    }

private void SetBusyState(bool isBusy)
    {
        _isBusy = isBusy;
        UpdateGlobalActionButtons();
        UpdateInspector();
    }

    private void UpdateGlobalActionButtons()
    {
        var canStartOperation = !_isBusy && _assignmentOperations.Count == 0;
        RefreshAllButton.IsEnabled = canStartOperation;
        EmptyAssignmentsActionButton.IsEnabled = canStartOperation;
        SignInButton.IsEnabled = canStartOperation;
        SettingsButton.IsEnabled = canStartOperation;
    }

    private void UpdateAssignmentOperations()
    {
        UpdateGlobalActionButtons();
        RefreshAssignmentList();
    }

    private static string GetAssignmentOperationKey(PimAssignment assignment) =>
        PimToolboxSettingsStore.ActivationKeys(assignment)[0];

    private async Task SaveSettingsAsync()
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var snapshot = new PimToolboxSettings
            {
                Tenants = [.. _settings.Tenants],
                FavoriteKeys = [.. _settings.FavoriteKeys],
                GlobalJustification = _settings.GlobalJustification,
                WorkStartTime = _settings.WorkStartTime,
                WorkEndTime = _settings.WorkEndTime,
                CloseAction = _settings.CloseAction,
                ActivationStartTimes = new Dictionary<string, DateTimeOffset>(
                    _settings.ActivationStartTimes, StringComparer.OrdinalIgnoreCase)
            };
            await _settingsStore.SaveAsync(snapshot);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void UpdateActionButtons()
    {
        var assignment = _selectedAssignment;
        var assignmentInProgress = assignment is not null &&
                                  _assignmentOperations.ContainsKey(GetAssignmentOperationKey(assignment));
        DeactivateButton.IsEnabled = !_isBusy && !assignmentInProgress && assignment is
        {
            Status: AssignmentStatus.Active,
            IsPermanent: false
        } && (assignment.StartTime is null ||
              DateTimeOffset.UtcNow - assignment.StartTime >= TimeSpan.FromMinutes(5));
    }

    private static string GetExpiryCaption(PimAssignment assignment) =>
        assignment.EndTime is null ? assignment.IsPermanent ? "no end date" : "no expiration" : "until expiry";

    private enum EmptyStateAction
    {
        ConfigureTenants,
        RefreshAll,
        ClearFilters
    }

    private sealed record AssignmentRow(PimAssignment Assignment, string? OperationStatus)
    {
        public string DisplayName => Assignment.DisplayName;
        public string ContextLine => $"{Assignment.TenantName}  ·  {Assignment.Family}" +
            (Assignment.IsGroupBacked ? "  ·  group-backed" : "");
        public bool IsOperationInProgress => OperationStatus is not null;
        public string StatusLabel => Assignment.Status == AssignmentStatus.Active ? "ACTIVE" : "ELIGIBLE";
        public IBrush StatusForeground => new SolidColorBrush(Color.Parse(
            Assignment.Status == AssignmentStatus.Active ? "#55B7F3" : "#AAA7F5"));
        public IBrush StatusBackground => new SolidColorBrush(Color.Parse(
            Assignment.Status == AssignmentStatus.Active ? "#102C40" : "#29274D"));
        public string ExpiryLabel => GetExpiry(Assignment);
        public string ExpiryCaption => GetExpiryCaption(Assignment);
        public IBrush ExpiryForeground => new SolidColorBrush(Color.Parse(
            Assignment.EndTime is { } end && end <= DateTimeOffset.UtcNow.AddHours(1)
                ? "#FFB86B"
                : "#D5DFED"));
    }

    private sealed record TenantNavigationItem(
        bool IsAll,
        string TenantId,
        string Name,
        string Detail,
        IBrush Accent,
        string Initials,
        IBrush DetailColor,
        string? Error);

    private static string ActionResultMessage(
        string action,
        PimAssignment assignment,
        PimActionResult result,
        TimeSpan? duration)
    {
        var detail = result.PendingApproval
            ? $"is pending approval ({result.Status})"
            : result.Verified
                ? "was verified in Microsoft Graph"
                : $"was accepted ({result.Status}) but is not yet visible in Microsoft Graph";
        var durationText = duration is null ? "" : $" for {duration.Value:g}";
        return $"{action} {detail}: {assignment.DisplayName} in {assignment.TenantName}{durationText}.";
    }

    private void UpdateTenantStatus()
    {
        RefreshTenantNavigation();
    }

    private async Task RefreshTenantAsync(TenantConfiguration tenant)
    {
        var refreshGate = _tenantRefreshGates.GetOrAdd(tenant.TenantId, _ => new SemaphoreSlim(1, 1));
        await refreshGate.WaitAsync();
        try
        {
            try
            {
                var session = await SignInAsync(tenant);
                var assignments = await _graph.RetrieveAsync(tenant, session.AccessToken);
                _snapshotStore.ApplyRefresh(tenant.TenantId, ApplyKnownActivationTimes(assignments), null);
            }
            catch (Exception error)
            {
                _snapshotStore.ApplyRefresh(tenant.TenantId, null, error);
                FooterText.Text = $"Refresh failed for {tenant.Name}: {error.Message}";
                ShowErrorBanner(FooterText.Text);
            }
            RefreshAssignmentList();
            UpdateTenantStatus();
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private IReadOnlyList<PimAssignment> ApplyKnownActivationTimes(IReadOnlyList<PimAssignment> assignments) =>
        assignments.Select(assignment =>
        {
            if (assignment.StartTime is not null)
                return assignment;
            var startTime = PimToolboxSettingsStore.ActivationKeys(assignment)
                .Select(key => _settings.ActivationStartTimes.TryGetValue(key, out var value) ? value : (DateTimeOffset?)null)
                .FirstOrDefault(value => value is not null);
            return startTime is { } knownStart ? assignment with { StartTime = knownStart } : assignment;
        }).ToArray();

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        RefreshAssignmentList();
        UpdateActionButtons();
        var now = DateTimeOffset.UtcNow;
        foreach (var assignment in _snapshotStore.GetMergedAssignments().Where(item =>
                     item.Status == AssignmentStatus.Active && item.EndTime is not null))
        {
            if (assignment.EndTime <= now || assignment.EndTime > now.AddMinutes(15))
                continue;
            var key = $"{assignment.TenantId}|{assignment.Family}|{assignment.Id}|{assignment.ResourceId}";
            if (!_notified.Add(key)) continue;
            if (!IsVisible) Show();
            Activate();
            FooterText.Text = $"Expiry warning: {assignment.DisplayName} in {assignment.TenantName} expires in {GetExpiry(assignment)}.";
        }
    }

    private async Task<bool> ConfirmAsync(string title, string details)
    {
        if (_confirmationCompletion is not null)
            throw new InvalidOperationException("Another confirmation dialog is already open.");

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmationCompletion = completion;
        var wasMainContentEnabled = MainContent.IsEnabled;
        ConfirmationTitle.Text = title;
        ConfirmationDetails.Text = details;
        MainContent.IsEnabled = false;
        ConfirmationOverlay.IsVisible = true;
        ConfirmationConfirmButton.Focus();
        try
        {
            return await completion.Task;
        }
        finally
        {
            ConfirmationOverlay.IsVisible = false;
            MainContent.IsEnabled = wasMainContentEnabled;
            if (wasMainContentEnabled)
                SearchBox.Focus();
            if (ReferenceEquals(_confirmationCompletion, completion))
                _confirmationCompletion = null;
        }
    }


    private void OnConfirmationCancelClick(object? sender, RoutedEventArgs e) =>
        _confirmationCompletion?.TrySetResult(false);

    private void OnConfirmationConfirmClick(object? sender, RoutedEventArgs e) =>
        _confirmationCompletion?.TrySetResult(true);

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            switch (_closeActionForSession ?? _settings.CloseAction)
            {
                case ExitCloseAction:
                    app.ExitApplication();
                    break;
                case MinimizeCloseAction:
                    Hide();
                    break;
                default:
                    ShowClosePrompt();
                    break;
            }

            return;
        }

        _countdownTimer.Stop();
        _httpClient.Dispose();
    }

    private void ShowClosePrompt()
    {
        MainContent.IsEnabled = false;
        SettingsOverlay.IsEnabled = false;
        ClosePromptOverlay.IsVisible = true;
        UpdateClosePromptAvailability();
    }

    private void UpdateClosePromptAvailability(string? message = null)
    {
        var canChoose = _settingsLoaded || _settingsLoadFailed;
        CloseExitButton.IsEnabled = canChoose;
        CloseToTrayButton.IsEnabled = canChoose;
        ClosePromptMessage.Text = message ?? (_settingsLoadFailed
            ? "Settings could not be loaded. Your choice will apply only for this session."
            : _settingsLoaded
                ? "Choose what the X button should do from now on. You can still exit from the tray menu."
                : "Loading settings. Close options will be available shortly.");
        if (canChoose && ClosePromptOverlay.IsVisible)
            CloseExitButton.Focus();
    }

    private async void OnExitCloseChoiceClick(object? sender, RoutedEventArgs e) =>
        await ApplyCloseActionAsync(ExitCloseAction);

    private async void OnMinimizeCloseChoiceClick(object? sender, RoutedEventArgs e) =>
        await ApplyCloseActionAsync(MinimizeCloseAction);

    private async Task ApplyCloseActionAsync(string closeAction)
    {
        if (!_settingsLoaded && !_settingsLoadFailed)
            return;

        CloseExitButton.IsEnabled = false;
        CloseToTrayButton.IsEnabled = false;
        if (_settingsLoaded)
        {
            var previousCloseAction = _settings.CloseAction;
            _settings.CloseAction = closeAction;
            try
            {
                await SaveSettingsAsync();
            }
            catch (Exception error)
            {
                _settings.CloseAction = previousCloseAction;
                UpdateClosePromptAvailability($"The close preference could not be saved: {error.Message}");
                return;
            }
        }

        _closeActionForSession = closeAction;
        ClosePromptOverlay.IsVisible = false;
        MainContent.IsEnabled = true;
        SettingsOverlay.IsEnabled = true;
        if (closeAction == ExitCloseAction)
        {
            var app = Application.Current as App
                ?? throw new InvalidOperationException("The desktop application lifetime is unavailable.");
            app.ExitApplication();
        }
        else
        {
            Hide();
        }
    }
}
