using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PIMToolbox.Desktop;

public sealed class TenantEditorModel : INotifyPropertyChanged
{
    private string _name;
    private string _tenantId;
    private string _clientId;
    private string _edgeExecutable;
    private string _edgeUserDataDirectory;
    private string _edgeProfileDirectory;
    private string _expectedUsername;

    public TenantEditorModel(
        string name = "",
        string tenantId = "",
        string clientId = "",
        string edgeExecutable = "",
        string edgeUserDataDirectory = "",
        string edgeProfileDirectory = "Default",
        string expectedUsername = "")
    {
        _name = name;
        _tenantId = tenantId;
        _clientId = clientId;
        _edgeExecutable = edgeExecutable;
        _edgeUserDataDirectory = edgeUserDataDirectory;
        _edgeProfileDirectory = edgeProfileDirectory;
        _expectedUsername = expectedUsername;
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string TenantId
    {
        get => _tenantId;
        set => SetField(ref _tenantId, value);
    }

    public string ClientId
    {
        get => _clientId;
        set => SetField(ref _clientId, value);
    }

    public ObservableCollection<EdgeProfileOption> EdgeProfiles { get; } = [];

    public EdgeProfileOption? SelectedEdgeProfile
    {
        get => EdgeProfiles.FirstOrDefault(option =>
            string.Equals(option.EdgeExecutable, EdgeExecutable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.UserDataDirectory, EdgeUserDataDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ProfileDirectory, EdgeProfileDirectory, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
                return;
            EdgeExecutable = value.EdgeExecutable;
            EdgeUserDataDirectory = value.UserDataDirectory;
            EdgeProfileDirectory = value.ProfileDirectory;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEdgeProfile)));
        }
    }

    public void SetEdgeProfiles(IEnumerable<EdgeProfileOption> options)
    {
        var availableProfiles = options.ToList();
        var selected = availableProfiles.FirstOrDefault(option =>
            string.Equals(option.EdgeExecutable, EdgeExecutable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.UserDataDirectory, EdgeUserDataDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.ProfileDirectory, EdgeProfileDirectory, StringComparison.OrdinalIgnoreCase));

        EdgeProfiles.Clear();
        foreach (var option in availableProfiles)
            EdgeProfiles.Add(option);

        if (selected is not null)
        {
            SelectedEdgeProfile = selected;
            return;
        }

        if (string.IsNullOrWhiteSpace(EdgeExecutable) && string.IsNullOrWhiteSpace(EdgeUserDataDirectory))
        {
            SelectedEdgeProfile = EdgeProfiles.FirstOrDefault(option => option.IsSystemBrowser);
            return;
        }

        var custom = new EdgeProfileOption(
            $"Custom: {EdgeProfileDirectory} ({EdgeUserDataDirectory})",
            EdgeExecutable,
            EdgeUserDataDirectory,
            EdgeProfileDirectory);
        EdgeProfiles.Add(custom);
        SelectedEdgeProfile = custom;
    }

    public string EdgeExecutable
    {
        get => _edgeExecutable;
        set
        {
            if (_edgeExecutable == value)
                return;
            SetField(ref _edgeExecutable, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEdgeProfile)));
        }
    }

    public string EdgeUserDataDirectory
    {
        get => _edgeUserDataDirectory;
        set
        {
            if (_edgeUserDataDirectory == value)
                return;
            SetField(ref _edgeUserDataDirectory, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEdgeProfile)));
        }
    }

    public string EdgeProfileDirectory
    {
        get => _edgeProfileDirectory;
        set
        {
            if (_edgeProfileDirectory == value)
                return;
            SetField(ref _edgeProfileDirectory, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEdgeProfile)));
        }
    }

    public string ExpectedUsername
    {
        get => _expectedUsername;
        set => SetField(ref _expectedUsername, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
