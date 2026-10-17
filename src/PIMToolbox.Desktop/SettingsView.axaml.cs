using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using PIMToolbox.Core;

namespace PIMToolbox.Desktop;

public partial class SettingsView : UserControl, INotifyPropertyChanged
{
    private PimToolboxSettings _originalSettings = new();
    private PimToolboxSettingsStore _settingsStore = new();
    private string _settingsFilePath = "";
    private string _globalJustification = "";
    private string _workStartTime = "";
    private string _workEndTime = "";
    private int _closeActionIndex;
    private const string ExitCloseAction = "Exit";
    private const string MinimizeCloseAction = "MinimizeToTray";

    public SettingsView()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void OnSettingsFieldGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid)
            return;

        var fields = grid.Children.OfType<Control>().ToArray();
        if (fields.Length == 2)
            UpdateFieldLayout(grid, fields[0], fields[1], e.NewSize.Width < 680);
    }

    private static void UpdateFieldLayout(Grid grid, Control firstField, Control secondField, bool isCompact)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(isCompact ? 0 : 1, isCompact ? GridUnitType.Pixel : GridUnitType.Star);
        grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Auto);
        grid.RowDefinitions[1].Height = new GridLength(isCompact ? 1 : 0, isCompact ? GridUnitType.Auto : GridUnitType.Pixel);
        Grid.SetRow(firstField, 0);
        Grid.SetColumn(firstField, 0);
        Grid.SetRow(secondField, isCompact ? 1 : 0);
        Grid.SetColumn(secondField, isCompact ? 0 : 1);
        firstField.Margin = isCompact ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        secondField.Margin = isCompact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    public SettingsView(PimToolboxSettings settings, PimToolboxSettingsStore settingsStore) : this()
    {
        LoadSettings(settings, settingsStore);
    }

    public void LoadSettings(PimToolboxSettings settings, PimToolboxSettingsStore settingsStore)
    {
        _originalSettings = settings;
        _settingsStore = settingsStore;
        SettingsFilePath = settingsStore.SettingsPath;
        ErrorText.Text = "";
        IReadOnlyList<EdgeProfileOption> discoveredProfiles;
        try
        {
            discoveredProfiles = EdgeProfileDiscovery.Discover();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            discoveredProfiles = [EdgeProfileOption.SystemBrowser];
            ErrorText.Text = $"Edge profiles could not be scanned: {error.Message}";
        }
        GlobalJustification = settings.GlobalJustification;
        WorkStartTime = settings.WorkStartTime;
        WorkEndTime = settings.WorkEndTime;
        CloseActionIndex = settings.CloseAction switch
        {
            ExitCloseAction => 1,
            MinimizeCloseAction => 2,
            _ => 0
        };
        SavedSettings = null;
        SaveButton.IsEnabled = true;
        CancelButton.IsEnabled = true;

        Tenants.Clear();
        foreach (var tenant in settings.Tenants)
        {
            var editor = new TenantEditorModel(
                tenant.Name,
                tenant.TenantId,
                tenant.ClientId,
                tenant.EdgeExecutable,
                tenant.EdgeUserDataDirectory,
                tenant.EdgeProfileDirectory,
                tenant.ExpectedUsername ?? "");
            editor.SetEdgeProfiles(discoveredProfiles);
            Tenants.Add(editor);
        }
    }

    public void FocusFirstField() => GlobalJustificationBox.Focus();

    public new event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CancelRequested;
    public event Action<PimToolboxSettings>? SettingsSaved;

    public string SettingsFilePath
    {
        get => _settingsFilePath;
        private set => SetField(ref _settingsFilePath, value);
    }

    public ObservableCollection<TenantEditorModel> Tenants { get; } = [];

    public PimToolboxSettings? SavedSettings { get; private set; }

    public string GlobalJustification
    {
        get => _globalJustification;
        set => SetField(ref _globalJustification, value);
    }

    public string WorkStartTime
    {
        get => _workStartTime;
        set => SetField(ref _workStartTime, value);
    }

    public string WorkEndTime
    {
        get => _workEndTime;
        set => SetField(ref _workEndTime, value);
    }

    public int CloseActionIndex
    {
        get => _closeActionIndex;
        set
        {
            if (_closeActionIndex == value)
                return;

            _closeActionIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseActionIndex)));
        }
    }

    private void OnAddTenantClick(object? sender, RoutedEventArgs e)
    {
        var tenant = new TenantEditorModel();
        try
        {
            tenant.SetEdgeProfiles(EdgeProfileDiscovery.Discover());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            tenant.SetEdgeProfiles([EdgeProfileOption.SystemBrowser]);
            ErrorText.Text = $"Edge profiles could not be scanned: {error.Message}";
        }

        Tenants.Add(tenant);
    }

    private void OnRemoveTenantClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TenantEditorModel tenant })
            Tenants.Remove(tenant);
    }

    private async void OnBrowseEdgeExecutableClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TenantEditorModel tenant })
            return;

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ErrorText.Text = "The file picker is unavailable in the current window.";
            return;
        }

        try
        {
            string? executablePath;
            if (OperatingSystem.IsMacOS())
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Microsoft Edge.app",
                    AllowMultiple = false
                });
                if (folders.Count == 0)
                    return;

                var appBundlePath = folders[0].Path.LocalPath;
                executablePath = Path.Combine(appBundlePath, "Contents", "MacOS", "Microsoft Edge");
            }
            else
            {
                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Microsoft Edge",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Microsoft Edge") { Patterns = ["msedge.exe"] }]
                });
                if (files.Count == 0)
                    return;
                executablePath = files[0].Path.LocalPath;
            }

            if (!File.Exists(executablePath))
            {
                ErrorText.Text = $"Microsoft Edge executable was not found at '{executablePath}'.";
                return;
            }

            tenant.EdgeExecutable = executablePath;
            RefreshTenantProfiles(tenant);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorText.Text = $"Microsoft Edge could not be selected: {error.Message}";
        }
    }

    private async void OnBrowseUserDataDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TenantEditorModel tenant })
            return;

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            ErrorText.Text = "The folder picker is unavailable in the current window.";
            return;
        }

        try
        {
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the Edge User Data folder",
                AllowMultiple = false
            });
            if (folders.Count == 0)
                return;

            tenant.EdgeUserDataDirectory = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory))
                tenant.EdgeProfileDirectory = "Default";
            if (string.IsNullOrWhiteSpace(tenant.EdgeExecutable))
                tenant.EdgeExecutable = EdgeProfileDiscovery.FindEdgeExecutable();
            RefreshTenantProfiles(tenant);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorText.Text = $"Edge profiles could not be read: {error.Message}";
        }
    }

    private void OnRefreshEdgeProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TenantEditorModel tenant })
            RefreshTenantProfiles(tenant);
    }

    private void RefreshTenantProfiles(TenantEditorModel tenant)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tenant.EdgeExecutable))
                tenant.EdgeExecutable = EdgeProfileDiscovery.FindEdgeExecutable();
            var profiles = EdgeProfileDiscovery.Discover().ToList();
            if (!string.IsNullOrWhiteSpace(tenant.EdgeUserDataDirectory))
            {
                if (string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory))
                    tenant.EdgeProfileDirectory = "Default";
                profiles.AddRange(EdgeProfileDiscovery.DiscoverFromUserDataDirectory(
                    tenant.EdgeUserDataDirectory, tenant.EdgeExecutable));
                if (profiles.All(option => option.IsSystemBrowser ||
                    !string.Equals(option.UserDataDirectory, tenant.EdgeUserDataDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    profiles.Add(new EdgeProfileOption(
                        $"Create or use Default profile ({tenant.EdgeUserDataDirectory})",
                        tenant.EdgeExecutable,
                        tenant.EdgeUserDataDirectory,
                        string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory) ? "Default" : tenant.EdgeProfileDirectory));
                }
            }

            tenant.SetEdgeProfiles(profiles.Distinct().ToArray());
            var match = tenant.EdgeProfiles.FirstOrDefault(option =>
                string.Equals(option.UserDataDirectory, tenant.EdgeUserDataDirectory, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.ProfileDirectory, tenant.EdgeProfileDirectory, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                tenant.SelectedEdgeProfile = match;
            ErrorText.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ErrorText.Text = $"Edge profiles could not be refreshed: {error.Message}";
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!TryCreateSettings(out var settings, out var validationError))
        {
            ErrorText.Text = validationError;
            return;
        }

        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            await _settingsStore.SaveAsync(settings);
            SavedSettings = settings;
            SettingsSaved?.Invoke(settings);
        }
        catch (Exception error)
        {
            ErrorText.Text = $"Settings could not be saved: {error.Message}";
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private bool TryCreateSettings(out PimToolboxSettings settings, out string error)
    {
        settings = new PimToolboxSettings();
        error = "";

        if (string.IsNullOrWhiteSpace(GlobalJustification))
        {
            error = "Default justification cannot be empty.";
            return false;
        }

        if (!TimeOnly.TryParse(WorkStartTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !TimeOnly.TryParse(WorkEndTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            error = "Enter valid workday start and end times, for example 08:00 and 17:00.";
            return false;
        }

        var tenants = new List<TenantConfiguration>(Tenants.Count);
        var tenantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tenant in Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.Name) ||
                string.IsNullOrWhiteSpace(tenant.TenantId) ||
                string.IsNullOrWhiteSpace(tenant.ClientId))
            {
                error = "Each tenant profile needs a name, tenant ID, and application client ID.";
                return false;
            }

            if (!tenantIds.Add(tenant.TenantId.Trim()))
            {
                error = $"Tenant ID '{tenant.TenantId.Trim()}' is configured more than once.";
                return false;
            }

            var hasEdgeExecutable = !string.IsNullOrWhiteSpace(tenant.EdgeExecutable);
            var hasEdgeDirectory = !string.IsNullOrWhiteSpace(tenant.EdgeUserDataDirectory);
            if (hasEdgeExecutable != hasEdgeDirectory)
            {
                error = $"Configure both Edge paths for '{tenant.Name}', or leave both empty to use the system browser.";
                return false;
            }

            var edgeExecutable = tenant.EdgeExecutable.Trim();
            var edgeUserDataDirectory = tenant.EdgeUserDataDirectory.Trim();
            var edgeProfileDirectory = string.IsNullOrWhiteSpace(tenant.EdgeProfileDirectory)
                ? "Default"
                : tenant.EdgeProfileDirectory.Trim();
            if (hasEdgeExecutable && (edgeProfileDirectory is "." or ".." ||
                edgeProfileDirectory.Contains('/') || edgeProfileDirectory.Contains('\\')))
            {
                error = $"Edge profile directory for '{tenant.Name}' must be a profile name, not a path.";
                return false;
            }

            if (hasEdgeExecutable && !edgeProfiles.Add(Path.GetFullPath(Path.Combine(
                edgeUserDataDirectory, edgeProfileDirectory))))
            {
                error = $"The Edge profile '{edgeProfileDirectory}' is selected for more than one tenant.";
                return false;
            }

            tenants.Add(new TenantConfiguration(
                tenant.Name.Trim(),
                tenant.TenantId.Trim(),
                tenant.ClientId.Trim(),
                edgeExecutable,
                edgeUserDataDirectory,
                edgeProfileDirectory,
                string.IsNullOrWhiteSpace(tenant.ExpectedUsername)
                    ? null
                    : tenant.ExpectedUsername.Trim()));
        }

        settings = new PimToolboxSettings
        {
            Tenants = tenants,
            FavoriteKeys = [.. _originalSettings.FavoriteKeys],
            GlobalJustification = GlobalJustification.Trim(),
            WorkStartTime = TimeOnly.Parse(WorkStartTime, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture),
            WorkEndTime = TimeOnly.Parse(WorkEndTime, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture),
            CloseAction = CloseActionIndex switch
            {
                1 => ExitCloseAction,
                2 => MinimizeCloseAction,
                _ => null
            },
            ActivationStartTimes = new Dictionary<string, DateTimeOffset>(
                _originalSettings.ActivationStartTimes,
                StringComparer.OrdinalIgnoreCase)
        };
        return true;
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
