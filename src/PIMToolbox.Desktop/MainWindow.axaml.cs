using System.Diagnostics;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PIMToolbox.Core;

namespace PIMToolbox.Desktop;

public partial class MainWindow : Window
{
    private readonly PimToolboxSettingsStore _settingsStore = new();
    private readonly TenantSnapshotStore _snapshotStore = new();
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, AuthenticationProof> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notified = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private PimToolboxSettings _settings = new();
    private GraphPimClient _graph = null!;
    private TenantActionRouter _actionRouter = null!;
    private PimAssignment? _selectedAssignment;
    private bool _isBusy;
    private bool _isUpdatingNavigation;
    private string? _tenantFilter;
    private CancellationTokenSource? _operationCancellation;
    private static readonly string[] TenantAccents = ["#0093E5", "#8E8BE8", "#55B7F3", "#6E6BCB", "#7C9FF2"];

    public MainWindow()
    {
        InitializeComponent();
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
            JustificationBox.Text = _settings.GlobalJustification;
            RefreshTenantNavigation();
            _actionRouter = new TenantActionRouter(_settings.Tenants, GetTokenAsync, _graph);
            FooterText.Text = _settings.Tenants.Count == 0
                ? $"Add tenant profiles to {_settingsStore.SettingsPath}, then restart the app."
                : $"Settings: {_settingsStore.SettingsPath}";
            UpdateTenantStatus();
        }
        catch (Exception error)
        {
            FooterText.Text = $"Settings could not be loaded: {error.Message}";
        }
    }

    private async void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            FooterText.Text = "Wait for the current tenant operation to finish or cancel it first.";
            return;
        }
        var tenant = _settings.Tenants.FirstOrDefault(item =>
            _tenantFilter is null || item.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase));
        if (tenant is null)
        {
            FooterText.Text = "Select a tenant workspace or configure a tenant first.";
            return;
        }
        try
        {
            await SignInAsync(tenant);
        }
        catch (Exception error)
        {
            FooterText.Text = $"Sign-in failed for {tenant.Name}: {error.Message}";
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        try
        {
            foreach (var tenant in _settings.Tenants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var session = await SignInAsync(tenant, cancellationToken);
                    var assignments = await _graph.RetrieveAsync(tenant, session.AccessToken, cancellationToken);
                    _snapshotStore.ApplyRefresh(tenant.TenantId, ApplyKnownActivationTimes(assignments), null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    _snapshotStore.ApplyRefresh(tenant.TenantId, null, error);
                }
                UpdateTenantStatus();
                RefreshAssignmentList();
            }
            FooterText.Text = "Refresh finished. A failed tenant retains its last successful assignment snapshot.";
        }
        catch (OperationCanceledException)
        {
            FooterText.Text = "Refresh cancelled. Previously retrieved tenant assignments were retained.";
        }
        finally
        {
            _isBusy = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            UpdateTenantStatus();
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
        _isBusy = true;
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
                return;
            _settings.GlobalJustification = justification;
            await _settingsStore.SaveAsync(_settings);
            var activationStarted = DateTimeOffset.UtcNow;
            var result = await _actionRouter.ActivateAsync(assignment, duration, justification);
            var actionMessage = ActionResultMessage("Activation", assignment, result, duration);
            if (!result.PendingApproval)
            {
                foreach (var key in PimToolboxSettingsStore.ActivationKeys(assignment))
                    _settings.ActivationStartTimes[key] = activationStarted;
                try { await _settingsStore.SaveAsync(_settings); }
                catch (Exception error)
                {
                    actionMessage += $" Local activation time could not be saved: {error.Message}";
                }
            }
            FooterText.Text = actionMessage;
            await RefreshTenantAsync(_settings.Tenants.Single(item =>
                item.TenantId.Equals(assignment.TenantId, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error)
        {
            FooterText.Text = $"Activation failed: {error.Message}";
        }
        finally { _isBusy = false; }
    }

    private async void OnDeactivateClick(object? sender, RoutedEventArgs e)
    {
        var assignment = _selectedAssignment;
        if (assignment is null || _actionRouter is null || _isBusy) return;
        _isBusy = true;
        try
        {
            if (!await ConfirmAsync("Confirm deactivation",
                    $"{assignment.DisplayName}\nTenant: {assignment.TenantName}"))
                return;
            var result = await _actionRouter.DeactivateAsync(assignment);
            var actionMessage = ActionResultMessage("Deactivation", assignment, result, null);
            if (result.Verified)
            {
                foreach (var key in PimToolboxSettingsStore.ActivationKeys(assignment))
                    _settings.ActivationStartTimes.Remove(key);
                try { await _settingsStore.SaveAsync(_settings); }
                catch (Exception error)
                {
                    actionMessage += $" Local activation time could not be cleared: {error.Message}";
                }
            }
            FooterText.Text = actionMessage;
            await RefreshTenantAsync(_settings.Tenants.Single(item =>
                item.TenantId.Equals(assignment.TenantId, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error)
        {
            FooterText.Text = $"Deactivation failed: {error.Message}";
        }
        finally { _isBusy = false; }
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
            await _settingsStore.SaveAsync(_settings);
            RefreshAssignmentList();
            UpdateFavoriteButton();
        }
        catch (Exception error)
        {
            FooterText.Text = $"Favorite could not be saved: {error.Message}";
        }
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e) => RefreshAssignmentList();

    private void OnSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) =>
        RefreshAssignmentList();

    private void OnStatusFilterChanged(object? sender, SelectionChangedEventArgs e) =>
        RefreshAssignmentList();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settingsStore.SettingsPath) { UseShellExecute = true });
            FooterText.Text = "Edit tenant profile fields in settings.v2.json, then restart the app.";
        }
        catch (Exception error)
        {
            FooterText.Text = $"Could not open settings: {error.Message}";
        }
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
        ActivateButton.IsEnabled = assignment?.Status == AssignmentStatus.Eligible;
        FavoriteButton.IsEnabled = assignment is not null;
        if (assignment is null)
        {
            InspectorTitle.Text = "Select an assignment";
            InspectorTenant.Text = "—";
            InspectorType.Text = "—";
            InspectorSource.Text = "—";
            InspectorExpiry.Text = "—";
            InspectorStatusBadge.IsVisible = false;
            ActionSafetyText.Text = "Actions are routed using each assignment’s own tenant.";
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

    private void RefreshTenantNavigation()
    {
        _isUpdatingNavigation = true;
        var entries = new List<TenantNavigationItem>
        {
            new(true, "", "All tenants", $"{_snapshotStore.GetMergedAssignments().Count} assignments",
                new SolidColorBrush(Color.Parse("#0093E5")), "P", new SolidColorBrush(Color.Parse("#91A0B4")))
        };
        for (var index = 0; index < _settings.Tenants.Count; index++)
        {
            var tenant = _settings.Tenants[index];
            var snapshot = _snapshotStore.Snapshots.FirstOrDefault(item =>
                item.TenantId.Equals(tenant.TenantId, StringComparison.OrdinalIgnoreCase));
            var detail = snapshot is null
                ? _sessions.ContainsKey(tenant.TenantId) ? "Signed in · not refreshed" : "Not connected"
                : snapshot.Error is not null
                    ? $"Refresh issue · {snapshot.Assignments.Count} cached"
                    : $"{snapshot.Assignments.Count} assignments · refreshed";
            var detailColor = snapshot?.Error is not null
                ? "#FFB86B"
                : _sessions.ContainsKey(tenant.TenantId) ? "#55B7F3" : "#8291A6";
            var accent = TenantAccents[index % TenantAccents.Length];
            var initials = new string(tenant.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2).Select(part => char.ToUpperInvariant(part[0])).ToArray());
            entries.Add(new TenantNavigationItem(false, tenant.TenantId, tenant.Name, detail,
                new SolidColorBrush(Color.Parse(accent)), initials, new SolidColorBrush(Color.Parse(detailColor))));
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
        var assignments = _snapshotStore.GetMergedAssignments();
        if (_tenantFilter is not null)
            assignments = assignments.Where(item =>
                item.TenantId.Equals(_tenantFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (FavoritesOnly.IsChecked == true)
            assignments = assignments.Where(item => _settings.FavoriteKeys.Contains(
                PimToolboxSettingsStore.FavoriteKey(item), StringComparer.OrdinalIgnoreCase)).ToArray();
        var search = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(search))
            assignments = assignments.Where(item =>
                item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.TenantName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Family.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.GroupSources?.Any(group => group.GroupName.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false))
                .ToArray();
        var status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (status is "Eligible" or "Active")
            assignments = assignments.Where(item => item.Status.ToString() == status).ToArray();

        var selected = _selectedAssignment;
        var rows = assignments.OrderBy(item => item.Status).ThenBy(item => item.DisplayName)
            .Select(item => new AssignmentRow(item)).ToArray();
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
        UpdateInspector();
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

    private void UpdateActionButtons()
    {
        var assignment = _selectedAssignment;
        DeactivateButton.IsEnabled = assignment is
        {
            Status: AssignmentStatus.Active,
            IsPermanent: false
        } && (assignment.StartTime is null ||
              DateTimeOffset.UtcNow - assignment.StartTime >= TimeSpan.FromMinutes(5));
    }

    private static string GetExpiryCaption(PimAssignment assignment) =>
        assignment.EndTime is null ? assignment.IsPermanent ? "no end date" : "no expiration" : "until expiry";

    private sealed record AssignmentRow(PimAssignment Assignment)
    {
        public string DisplayName => Assignment.DisplayName;
        public string ContextLine => $"{Assignment.TenantName}  ·  {Assignment.Family}" +
            (Assignment.IsGroupBacked ? "  ·  group-backed" : "");
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
        IBrush DetailColor);

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
        }
        RefreshAssignmentList();
        UpdateTenantStatus();
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
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = details, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" };
        var confirm = new Button { Content = "Confirm" };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _countdownTimer.Stop();
            _httpClient.Dispose();
        }
    }
}
