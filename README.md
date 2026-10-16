# PIM Toolbox

PIM Toolbox is a native cross-platform Avalonia desktop app for managing Microsoft Entra PIM assignments. The former Windows PowerShell/WPF implementation has been removed; the maintained application lives in `src/`.

The new client is split into:

- `PIMToolbox.Desktop`: Avalonia command-center UI with a tenant identity rail, searchable assignment view, expiry summaries, contextual inspector, tray/menu-bar lifecycle, favorites, and confirmations.
- `PIMToolbox.Core`: MSAL authentication, protected token cache, Graph access, PIM normalization, tenant aggregation, and action routing.
- `PIMToolbox.AuthPoc`: a small interactive authentication and read-only Graph proof-of-concept runner.
- `tests/PIMToolbox.Core.Tests`: focused domain and action-routing tests.

The current PIM scope remains Entra directory roles and PIM for Groups, not Azure resource PIM.

The desktop UI uses Avalonia's built-in controls and Fluent theme rather than adding a separate third-party control suite. This keeps the Windows/macOS shell lightweight while leaving the PIM-specific assignment cards, tenant indicators, and expiry states custom-styled. The supplied transparent 512×512 logo is stored in `src/PIMToolbox.Desktop/Assets` as `PIMToolbox.svg` with a Windows `.ico` variant. The SVG is rendered in the header and for the tray/menu-bar icon; the `.ico` is embedded in the Windows executable. The interface palette is based on the logo's blue `#0093E5` and indigo `#232171`, with dark neutral surfaces for contrast.

## Requirements and build

- Windows 10/11 or macOS; .NET 8 SDK.
- Microsoft Edge for the configured-profile sign-in path, or a system browser for the documented fallback.
- Internet access to Microsoft Entra ID and Microsoft Graph.
- PowerShell is **not** required by the replacement.

From the repository root:

```powershell
dotnet restore PIMToolbox.sln
dotnet build PIMToolbox.sln -c Release
dotnet test tests\PIMToolbox.Core.Tests\PIMToolbox.Core.Tests.csproj -c Release
dotnet run --project src\PIMToolbox.Desktop\PIMToolbox.Desktop.csproj
```

The project targets `net8.0`; Avalonia publishes for both operating systems from the same source.

To create and run a self-contained Windows x64 app folder from the repository root:

```powershell
$publishDir = Join-Path (Get-Location) 'artifacts\windows\win-x64'
dotnet publish src\PIMToolbox.Desktop\PIMToolbox.Desktop.csproj `
  -c Release -r win-x64 --self-contained true -o $publishDir
& (Join-Path $publishDir 'PIMToolbox.Desktop.exe')
```

Run the executable from inside the publish folder or keep the entire folder together; the `.exe` depends on the adjacent published runtime and Avalonia files. Generated `bin`, `obj`, and `artifacts` directories are ignored by Git.

## Entra app registration and permissions

Create a **public client** app registration in each tenant, or use one public-client registration that is available in all configured tenants:

1. Add the **Mobile and desktop applications** platform with `http://localhost` as a redirect URI.
2. Enable public client flows if the tenant's app-registration settings require it.
3. Add these Microsoft Graph **delegated** permissions:
   - `User.Read`
   - `Group.Read.All` (to resolve role-assignable transitive group membership and group names)
   - `RoleManagement.ReadWrite.Directory`
   - `PrivilegedAccess.ReadWrite.AzureADGroup`
4. Grant tenant admin consent where required by tenant policy.

The client uses authorization-code + PKCE through MSAL. It has no client secret and no server-side token broker. MSAL owns the loopback redirect listener, PKCE/state validation, code exchange, and silent token renewal. Each tenant gets a separate MSAL cache; Windows uses current-user DPAPI protection and macOS uses Keychain-backed protection. The cache is stored locally under the application data directory.

## Tenant and Edge profile configuration

On first launch the app creates:

- Windows: `%LOCALAPPDATA%\PIMToolbox\settings.v2.json`
- macOS: `~/Library/Application Support/PIMToolbox/settings.v2.json`

Edit `Tenants` in this file, then restart the app. Configure one unique Edge user-data directory per tenant. Paths and Edge executable locations are **device-local settings**, not portable profile names. Example:

```json
{
  "Tenants": [
    {
      "Name": "Contoso",
      "TenantId": "11111111-1111-1111-1111-111111111111",
      "ClientId": "22222222-2222-2222-2222-222222222222",
      "ExpectedUsername": "admin@contoso.example",
      "EdgeExecutable": "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
      "EdgeUserDataDirectory": "C:\\Users\\you\\AppData\\Local\\PIMToolbox\\EdgeProfiles\\contoso",
      "EdgeProfileDirectory": "Default"
    },
    {
      "Name": "Fabrikam",
      "TenantId": "33333333-3333-3333-3333-333333333333",
      "ClientId": "22222222-2222-2222-2222-222222222222",
      "ExpectedUsername": "admin@fabrikam.example",
      "EdgeExecutable": "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
      "EdgeUserDataDirectory": "C:\\Users\\you\\AppData\\Local\\PIMToolbox\\EdgeProfiles\\fabrikam",
      "EdgeProfileDirectory": "Default"
    }
  ],
  "FavoriteKeys": [],
  "GlobalJustification": "PIM Quick Action",
  "WorkStartTime": "08:00",
  "WorkEndTime": "17:00"
}
```

On macOS, use the full executable path, commonly `/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge`, and absolute, unique user-data paths such as `/Users/you/Library/Application Support/PIMToolbox/EdgeProfiles/contoso`. Set `EdgeProfileDirectory` to the profile directory Edge should open (often `Default` for a new isolated profile).

The app passes the explicitly configured `--user-data-dir` and `--profile-directory` arguments to Edge; it does not infer profile names, scrape undocumented browser files, or read, copy, export, or share cookies. A dedicated user-data directory per tenant is the reliable configuration: sign into the intended identity in that Edge profile once, then subsequent interactive flows use that profile. If pointing at an existing shared Edge `User Data` tree, the directory/profile names must be entered by the user and may be affected by Edge process reuse or local Edge changes; that mode is not guaranteed by this project.

If both Edge profile fields are left empty, the app opens the operating-system default browser instead. This fallback still uses a tenant-specific MSAL authority/cache and requires an `ExpectedUsername` match before Graph data is accepted, but it does not isolate browser cookies. The Graph tenant ID, returned MSAL tenant ID, configured expected username, and Graph `/me` account are checked before treating sign-in as successful.

### Authentication proof of concept

The console POC uses the same MSAL/profile-launch implementation as the desktop app and makes only the read-only `GET /me` Graph call after sign-in:

```powershell
dotnet run --project src\PIMToolbox.AuthPoc\PIMToolbox.AuthPoc.csproj
# Or pass a settings file explicitly:
dotnet run --project src\PIMToolbox.AuthPoc\PIMToolbox.AuthPoc.csproj -- C:\path\to\settings.v2.json
```

This verifies the application builds with MSAL's custom browser callback and that the configured Edge paths become explicit launch arguments. A live authorization-code redirect, account check, Graph token, and `/me` response still require a real app registration, tenant consent, and an interactive user sign-in. Those external steps have not been performed as part of this repository build. No claim is made that the app reuses or inspects Edge cookies.

## Using the desktop app

1. Configure at least one tenant and its expected username in `settings.v2.json`.
2. Click **Sign in / verify** for one tenant, or **Refresh all tenants** to authenticate and retrieve each tenant independently.
3. The merged list identifies the tenant on every assignment. Retrieval failures remain visible for that tenant and retain its last successful data.
4. Select an eligible assignment, optionally enter an ISO-8601 duration (for example `PT2H`) and justification, then confirm activation. If duration is blank, the app uses the workday remainder bounded by the policy maximum; when no policy is returned, the legacy 8-hour default is used.
5. Time-bound active assignments can be confirmed and deactivated; permanent active assignments are not offered for deactivation. The app blocks deactivation during the first five minutes after the recorded activation start; activation timestamps are kept in local settings so the lockout survives app restarts, with Graph schedule-instance start times used when available. It reports approval-pending requests separately and only reports an action as verified after observing the expected Graph schedule-instance state. A group-backed role is routed through its unique eligible/active group source when available; ambiguous sources are not silently activated as direct roles.
6. Use **Toggle favorite** and **Favorites only** to manage the local favorites view. Closing the main window hides the app. The tray icon/menu-bar item can restore or exit it.

The app reads eligible and active role/group schedule instances, paginates Graph responses, retries throttling/transient service responses, resolves role-assignable transitive groups, and reconciles group-backed role sources. Actions resolve the tenant from the assignment itself and use that tenant's token; a mismatched/unknown tenant is rejected.

The countdown refreshes while the app is running, including while hidden to the tray. When an assignment is within 15 minutes of expiry, the app restores its window and presents an in-app warning. Native OS notification delivery is not yet implemented; the app-level alert is the current replacement for the original Windows warning.

Avalonia documents its `TrayIcon` as supported on Windows and macOS, where the macOS implementation appears as a menu-bar item. The UI does not depend on PowerShell, WPF, or Windows-only APIs.

## Existing settings and favorites migration

The previous Windows app stored `%LOCALAPPDATA%\PIMToolbox\settings.json` with `Tenants`, `FavoriteRoles`, `GlobalJustification`, `WorkStartTime`, and `WorkEndTime`. On Windows, the Avalonia app reads that existing file once when `settings.v2.json` does not exist:

- tenant names and IDs are carried forward;
- favorite identity is migrated from `TenantId`, `Id`, and `ResourceId`;
- justification and working-hour values are carried forward;
- the legacy file is left untouched.

Tenant IDs and tenant-specific app IDs are not embedded as repository defaults. Configure tenant IDs, client IDs, and profile paths locally in `settings.v2.json`; do not commit tenant configuration or IDs.

The old settings do not contain an app client ID, expected user, or Edge profile mapping. Add those fields to each migrated tenant before signing in. The old DPAPI refresh-token files are intentionally **not** imported; authenticate once per tenant so MSAL creates its separately protected cache. The old active-role countdown cache is not needed for countdowns because the replacement reads active end times from Graph. The five-minute deactivation lockout starts tracking activations made by the replacement app; already-active assignments use Graph's start time when provided. Legacy session activation records are not imported, so wait five minutes before deactivating any just-activated assignment after switching apps if its Graph start time is unavailable.

## Verification and known limits

Verified in this repository: all solution tests and the solution Release build, plus Avalonia desktop builds targeting Windows host and macOS `osx-arm64` and `osx-x64`. Tests cover normalization, inherited/group-backed dependency reconciliation, failed-refresh retention and cross-tenant merging, working-hours/policy duration calculations, tenant-specific action routing, group-backed activation routing and timestamp correlation, five-minute deactivation protection, permanent-assignment protection, and action approval/verification outcomes.

Not verified here: live Entra sign-in/consent, actual Edge launch/redirect completion, Graph PIM API permissions and tenant policy outcomes, execution on macOS, native notification delivery, or signed installers. These need real tenant configuration and platform-specific validation. In particular, a profile launch command is built and tested, but an authenticated sign-in in that Edge profile has not been demonstrated in this environment.

## Packaging guidance

### Windows installer

Publish a self-contained Windows build:

```powershell
$publishDir = Join-Path (Get-Location) 'artifacts\windows\win-x64'
dotnet publish src\PIMToolbox.Desktop\PIMToolbox.Desktop.csproj `
  -c Release -r win-x64 --self-contained true -o $publishDir
```

The executable is `artifacts\windows\win-x64\PIMToolbox.Desktop.exe`. Package the **entire** publish directory with WiX Toolset v4 or the MSIX Packaging Tool. The generated Windows icon is embedded in the app executable. Set a stable application identity for upgrade/uninstall behavior, and sign the installer/MSIX with an organization-controlled code-signing certificate. For an MSIX, the package publisher must match the signing certificate. Sign traditional installers with `signtool` and timestamp the signature.

### macOS signed and notarized app

Publish separately for Apple Silicon and Intel as required:

```bash
dotnet publish src/PIMToolbox.Desktop/PIMToolbox.Desktop.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish src/PIMToolbox.Desktop/PIMToolbox.Desktop.csproj -c Release -r osx-x64 --self-contained true
```

Bundle the publish output into `PIMToolbox.app/Contents/MacOS`, provide an `Info.plist` with bundle identifier/version, and include required Avalonia resources. Sign with a **Developer ID Application** certificate using hardened runtime and a timestamp. Submit the signed `.app`/DMG/ZIP with `xcrun notarytool submit --wait`, then staple the notarization ticket with `xcrun stapler staple`. Test Gatekeeper launch and Edge profile launching on a clean macOS account.

## Historical architecture reference

The historical behavior of the former PowerShell/WPF application is summarized in [`docs/entra-id-pim-architektur-referenz.md`](./docs/entra-id-pim-architektur-referenz.md). It is a domain reference, not a current executable or supported implementation.

## License

MIT. PIM Toolbox is not an official Microsoft product. Use it only in accordance with your organization's security and access policies.
