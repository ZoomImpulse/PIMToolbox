# PIM Toolbox

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Language](https://img.shields.io/badge/language-PowerShell-5391FE)
![License](https://img.shields.io/badge/license-MIT-green)

**PIM Toolbox** is a modern, PowerShell-based GUI application designed to streamline the management and activation of Entra ID Privileged Identity Management (PIM) roles and groups.

Built with Windows Presentation Foundation (WPF) and PowerShell, it provides a fast, dark-themed interface to manage your privileged access across multiple tenants without needing to navigate the Azure Portal.

<img width="1920" height="1032" alt="grafik" src="https://github.com/user-attachments/assets/c0fd418e-41bc-49b1-a167-71afb03feb32" />


## Table of Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Usage](#usage)
- [Configuration & Data](#configuration--data)
- [Permissions](#permissions)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Features

- **Modern Dark UI**: A clean, responsive interface built with WPF and styled for dark mode enthusiasts.
- **Multi-Tenant Support**: Easily switch between different Azure AD tenants.
- **Quick Activation**: "Star" your frequently used roles to pin them to the dashboard for one-click activation.
- **Role & Group Support**: Manages both Directory Roles and PIM for Groups.
- **Dependency Handling**: Automatically detects and activates PIM Groups required for specific role assignments.
- **Active Assignment Tracking**: View currently active roles, their expiration times, and deactivate them when finished.
- **Secure Authentication**:
  - Uses OAuth2 Authorization Code flow with **PKCE** (Proof Key for Code Exchange) for enhanced security.
  - Securely caches Refresh Tokens using **Windows DPAPI** (Data Protection API) via `System.Security.Cryptography.ProtectedData`.
- **Smart Policies**: Fetches role-specific expiration policies to automatically suggest the maximum allowed duration.

## Prerequisites

- **Operating System**: Windows 10 or Windows 11 (Required for WPF support).
- **PowerShell**: PowerShell 7+ (Core).
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

1.  Launch the application once to generate the default configuration file.
2.  Navigate to `%LOCALAPPDATA%\PIMToolbox\` and open `settings.json` in a text editor.
3.  Add your organization's details to the `Tenants` list:
    ```json
    { "Name": "Contoso", "Id": "your-tenant-guid" }
    ```
4.  Restart the application.
5.  (Optional) Use the in-app **Settings** button to configure **Global Justification** and **Working Hours**.

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
  - **Encrypted Tokens**: `%LOCALAPPDATA%\PIMToolbox\Tokens\*.dat` (Encrypted with current user credentials via DPAPI).

## Permissions

The application requests the following Microsoft Graph scopes to function:

- `User.Read`
- `RoleManagement.ReadWrite.Directory` (To read and activate directory roles)
- `PrivilegedAccess.ReadWrite.AzureADGroup` (To read and activate PIM groups)
- `offline_access` (To maintain sessions via refresh tokens)

## Troubleshooting

- **Script requires .NET assembly**: Ensure you are running on Windows. Linux/macOS are not supported due to the WPF GUI dependencies.

- **Authentication Loop**: If you get stuck in a loop, try deleting the `%LOCALAPPDATA%\PIMToolbox\Tokens` folder.

- **State Mismatch Error**: This occurs if the authentication flow is interrupted. Restart the application and try again.

## License

This project is licensed under the MIT License.

---

_Disclaimer: This tool is not an official Microsoft product. Use it at your own risk. Always ensure you comply with your organization's security policies regarding third-party tools and token caching._
