# PIM Toolbox

**PIM Toolbox** is a modern, PowerShell-based GUI application designed to streamline the management and activation of Entra ID Privileged Identity Management (PIM) roles and groups.

Built with Windows Presentation Foundation (WPF) and PowerShell, it provides a fast, dark-themed interface to manage your privileged access across multiple tenants without needing to navigate the Azure Portal.

## Features

- **Modern Dark UI**: A clean, responsive interface built with WPF and styled for dark mode enthusiasts.
- **Multi-Tenant Support**: Easily switch between different Azure AD tenants.
- **Quick Activation**: "Star" your frequently used roles to pin them to the dashboard for one-click activation.
- **Role & Group Support**: Manages both Directory Roles and PIM for Groups.
- **Dependency Handling**: Automatically detects and activates PIM Groups required for specific role assignments.
- **Active Assignment Tracking**: View currently active roles, their expiration times, and deactivate them when finished.
- **Secure Authentication**:
  - Uses OAuth2 Device Code/Authorization Code flow.
  - Securely caches Refresh Tokens using **Windows PasswordVault** (Credential Manager) where available, falling back to DPAPI encrypted files in `%LOCALAPPDATA%`.
- **Smart Policies**: Fetches role-specific expiration policies to automatically suggest the maximum allowed duration.

## Prerequisites

- **Operating System**: Windows 10 or Windows 11 (Required for WPF support).
- **PowerShell**: Windows PowerShell 5.1 or PowerShell 7+ (Core).
- **Network**: Internet access to `login.microsoftonline.com` and `graph.microsoft.com`.

## Installation

1.  Clone this repository or download the script file.
    ```bash
    git clone https://github.com/yourusername/PIMToolbox.git
    ```
2.  Navigate to the directory.
    ```powershell
    cd PIMToolbox
    ```
3.  Run the script.
    ```powershell
    .\PIMToolbox.ps1
    ```

## Usage

### 1. Initial Configuration

On the first launch, the application will load with default settings.

1.  Click the **Settings** button in the bottom left.
2.  Under **Tenants**, add your organization's details:
    - **Name**: A friendly name (e.g., "Contoso").
    - **Tenant ID**: The GUID of your Azure AD Tenant.
3.  (Optional) Set a **Global Justification** string (e.g., "Daily Operations") to pre-fill activation requests.
4.  Click **Close** to save.

### 2. Activating Roles

1.  Select your tenant from the dropdown menu.
2.  Click **Retrieve Roles**.
3.  A browser window will open for Microsoft authentication.
    - _Note: The app uses the standard "Microsoft Graph PowerShell" client ID, so you may need to consent to permissions on the first run._
4.  Once loaded, the **Eligible** tab displays available roles.
5.  Select a role and click **Activate Role**.

### 3. Quick Access (Favorites)

1.  In the **Eligible** list, click the **Star** icon next to a role or group.
2.  The role will appear in the **Quick Activate** panel at the top of the dashboard.
3.  Clicking the large button in the Quick Activate panel will immediately attempt to activate that role with the maximum allowed duration.

### 4. Deactivation

1.  Switch to the **Active** tab.
2.  Select an active assignment.
3.  Click **Deactivate Role**.

## Configuration & Data

Application settings and cached tokens are stored locally on your machine:

- **Settings**: `%LOCALAPPDATA%\PIMToolbox\settings.json`
- **Token Cache**:
  - **Windows Credential Manager**: Stored under the resource `PIMToolbox`.
  - **Fallback**: Encrypted XML files in `%LOCALAPPDATA%\PIMToolbox\`.

## Permissions

The application requests the following Microsoft Graph scopes to function:

- `User.Read`
- `RoleManagement.ReadWrite.Directory` (To read and activate directory roles)
- `PrivilegedAccess.ReadWrite.AzureADGroup` (To read and activate PIM groups)
- `offline_access` (To maintain sessions via refresh tokens)

## Troubleshooting

- **Script requires .NET assembly**: Ensure you are running on Windows. Linux/macOS are not supported due to the WPF GUI dependencies.
- **Authentication Loop**: If you get stuck in a loop, try clearing the cached credentials in Windows Credential Manager (look for `PIMToolbox`) or delete the `%LOCALAPPDATA%\PIMToolbox` folder.
- **State Mismatch Error**: This occurs if the authentication flow is interrupted. Restart the application and try again.

## License

This project is licensed under the MIT License.

---

_Disclaimer: This tool is not an official Microsoft product. Use it at your own risk. Always ensure you comply with your organization's security policies regarding third-party tools and token caching._
