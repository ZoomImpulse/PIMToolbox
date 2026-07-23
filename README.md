# PIM Toolbox

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Language](https://img.shields.io/badge/language-PowerShell-5391FE)
![License](https://img.shields.io/badge/license-MIT-green)

**PIM Toolbox** is a modern, PowerShell-based GUI application designed to streamline the management and activation of Entra ID Privileged Identity Management (PIM) roles and groups.

Built with Windows Presentation Foundation (WPF) and PowerShell, it provides a fast, dark-themed interface to manage your privileged access across multiple tenants without needing to navigate the Azure Portal.

Current script version: **2.1.0**


## Table of Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Usage](#usage)
- [Configuration & Data](#configuration--data)
- [Permissions](#permissions)
- [Architecture Reference](#architecture-reference)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Features

- **Modern Dark UI**: A clean, responsive interface built with WPF and styled for dark mode enthusiasts.
- **Multi-Tenant Support**: Easily switch between different Azure AD tenants.
- **Unified Assignment Retrieval**: Load eligible and active assignments with one action using **Retrieve Assignments**.
- **Role & Group Support**: Manages both directory roles and PIM for groups.
- **Favorites Dashboard**: Star assignments to pin them in **My Favorites**, grouped by tenant, with quick activate/remove actions.
- **Dependency-Aware Activation**: If a role is inherited through a PIM group, activation is redirected to the parent group when required.
- **Activation Confirmation + Smart Duration**:
    - Shows a confirmation dialog with role, duration, and justification.
    - Uses working-hours settings to auto-select an end time.
    - Lets you edit duration and justification before activation.
- **Active Assignment Tracking**: Shows live countdowns, urgency indicators, and supports deactivation.
- **Background Run Support**: Minimizes to system tray and can warn about assignments expiring soon.
- **Built-In Logging**: Open the log file from the UI; log files are automatically rotated when they exceed 10 MB.
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
5.  (Optional) Use the in-app **Settings** button to configure:
    - **Global Justification**
    - **Work Day Start**
    - **Work Day End**

### 2. Activating Assignments

1.  Select your tenant from the dropdown menu.
2.  Click **Retrieve Assignments**.
3.  A browser window will open for Microsoft authentication.
    - _Note: The app uses the standard "Microsoft Graph PowerShell" client ID, so you may need to consent to permissions on the first run._
4.  Once loaded, use the **Eligible** column to select an assignment.
5.  Confirm activation.
6.  If needed, choose **Edit Details** to set a custom duration and justification.

### 3. Quick Access (Favorites)

1.  In the **Eligible** list, click the **Star** icon next to a role or group.
2.  The assignment appears in the **My Favorites** panel, grouped by tenant.
3.  Use **Activate** to start activation from favorites.
4.  Use **X** to remove an entry from favorites.

### 4. Deactivation

1.  In the **Active** column, select an active assignment.
2.  Confirm deactivation in the dialog.

### 5. System Tray Behavior

1.  Closing the window (X) hides the app to the system tray by default.
2.  Double-click the tray icon (or use the tray context menu) to restore the window.
3.  Use **Exit** in the tray menu to fully close the app.

## Configuration & Data

Application settings, token cache, and logs are stored locally on your machine:

- **Settings**: `%LOCALAPPDATA%\PIMToolbox\settings.json`
- **Token Cache**:
  - **Encrypted Tokens**: `%LOCALAPPDATA%\PIMToolbox\Tokens\*.dat` (Encrypted with current user credentials via DPAPI).
- **Logs**:
    - `%LOCALAPPDATA%\PIMToolbox\logs\PIMToolbox.log`
    - Rotated automatically when file size exceeds 10 MB.

## Permissions

The application requests the following Microsoft Graph scopes to function:

- `User.Read`
- `RoleManagement.ReadWrite.Directory` (To read and activate directory roles)
- `PrivilegedAccess.ReadWrite.AzureADGroup` (To read and activate PIM groups)
- `offline_access` (To maintain sessions via refresh tokens)

## Architecture Reference

- Entra ID PIM architecture reference for this project:
    - `docs/entra-id-pim-architektur-referenz.md`

## Troubleshooting

- **Script requires .NET assembly**: Ensure you are running on Windows. Linux/macOS are not supported due to the WPF GUI dependencies.

- **Authentication Loop**: If you get stuck in a loop, try deleting the `%LOCALAPPDATA%\PIMToolbox\Tokens` folder.

- **State Mismatch Error**: This occurs if the authentication flow is interrupted. Restart the application and try again.

- **Authentication Timeout/Cancellation**: If browser sign-in takes too long or is cancelled, start **Retrieve Assignments** again.

- **No Tray Icon**: If the system tray icon is unavailable, ensure Windows Forms assemblies can be loaded in your environment.

## License

This project is licensed under the MIT License.

---

_Disclaimer: This tool is not an official Microsoft product. Use it at your own risk. Always ensure you comply with your organization's security policies regarding third-party tools and token caching._

