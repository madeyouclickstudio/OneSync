# OneSync

OneSync presents Microsoft 365 OneDrive and SharePoint document libraries as
real Windows drive letters (H:, I:, J:, …) backed by a Dokan user-mode
filesystem. Files appear immediately on logon, sync in the background via
Microsoft Graph, and Office files opened from a OneSync drive get
co-authoring and AutoSave by way of a built-in `ms-office:` URL redirect.

This README is the configuration and operations reference shipped alongside
the binary. For the annotated source-of-truth, see `config.template.json` in
the same folder.

---

## Contents

- [Prerequisites](#prerequisites)
- [First-time setup](#first-time-setup)
- [Configuration reference](#configuration-reference)
  - [Top-level](#top-level)
  - [`drives[]`](#drives)
  - [`driveFiltering`](#drivefiltering)
  - [`cleanup`](#cleanup)
  - [`syncSettings`](#syncsettings)
  - [`logging`](#logging)
- [Deployment](#deployment)
- [Logs and troubleshooting](#logs-and-troubleshooting)

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 1809 (build 17763) or later, 64-bit | OneSync is built as `win-x64`. |
| Dokan 2.x runtime | The MSI installs Dokan if it isn't present. |
| Microsoft Office 2016+ (optional) | Only needed if you want co-authoring/AutoSave on Office files. |
| An Entra ID (Azure AD) app registration | See below. |

### Entra ID app registration

Register a single multi-tenant or single-tenant app in your tenant's **Entra
admin centre → App registrations**:

1. **Platform:** add a *Mobile and desktop applications* platform with the
   redirect URI `ms-appx-web://microsoft.aad.brokerplugin/<clientId>` (so
   WAM SSO works) and `http://localhost`.
2. **Authentication:** set *Allow public client flows* to **Yes**.
3. **API permissions (delegated):**
   - `Files.ReadWrite` — read/write the user's OneDrive
   - `Sites.ReadWrite.All` — read/write SharePoint document libraries
   - `offline_access` — refresh tokens for silent SSO
   - `User.Read` — basic profile
4. Grant admin consent for those permissions.
5. Copy the **Tenant ID** and **Application (client) ID** into `config.json`.

---

## First-time setup

1. Install the MSI on a target machine. Files land in
   `C:\Program Files\OneSync\`.
2. Edit `C:\Program Files\OneSync\config.json` and replace the three
   placeholder GUIDs (`tenantId`, `clientId`, `authority`) with your tenant's
   values.
3. Add or remove `drives[]` entries to match the OneDrives and SharePoint
   libraries you want exposed.
4. Sign out and back in — the public Desktop shortcut and Common Startup
   shortcut launch OneSync on logon. The first run prompts the user to sign
   in via the Windows account broker (WAM); subsequent logons are silent.

> **Multi-machine rollouts:** for organisations, treat `config.json` as the
> only file you need to customise. Pass `-ConfigFile <path>` to
> `Install-OneSync.ps1` to swap in a customised config during install.

---

## Configuration reference

`config.json` accepts `//` line comments and trailing commas (it is parsed
as JSONC). All paths support `%LOCALAPPDATA%` / `%PROGRAMDATA%` /
`%USERPROFILE%` environment variables.

### Top-level

| Key | Type | Default | Meaning |
|---|---|---|---|
| `tenantId` | GUID | — | Your Microsoft 365 tenant ID. |
| `clientId` | GUID | — | Application (client) ID of the Entra app registration. |
| `authority` | URL | `https://login.microsoftonline.com/{tenantId}` | MSAL authority URL. Leave as the default unless you have a sovereign-cloud tenant. |
| `localStorageRoot` | path | `%LOCALAPPDATA%\OneSync\Drives` | Where OneSync stores the local NTFS-backed drive folders, sync queue DB, and metadata DB. Use `%PROGRAMDATA%` for shared-machine deployments where per-user cleanup should not happen. |

### `drives[]`

Every entry creates a Windows drive letter. Drives the signed-in user
doesn't have permission to are skipped silently at startup (see
[`driveFiltering`](#drivefiltering)).

| Key | Type | Required | Meaning |
|---|---|---|---|
| `letter` | char | yes | Windows drive letter (e.g. `H`). Must not collide with an existing drive. |
| `label` | string | yes | Display label in Explorer. |
| `type` | `"onedrive"` \| `"sharepoint"` | yes | What kind of cloud source this drive points to. |
| `remotePath` | string | yes | Subpath within the drive, usually `/` for the root. |
| `syncMode` | `"bidirectional"` | yes | Currently only `bidirectional` is supported. |
| `priority` | int | yes | Lower = higher priority, used by the upload scheduler when multiple drives have pending uploads. |
| `folderRedirection` | string[] | onedrive only | Windows shell folders to redirect onto this drive. Each entry creates the folder and points the matching User Shell Folder at it. Use `[]` to disable. |
| `siteUrl` | URL | sharepoint only | Full URL of the SharePoint site, e.g. `https://contoso.sharepoint.com/sites/staff`. |
| `libraryName` | string | sharepoint only | Document library name within the site, typically `Shared Documents`. |

**OneDrive example:**

```json
{
  "letter": "H",
  "label": "Home Folder",
  "type": "onedrive",
  "remotePath": "/",
  "syncMode": "bidirectional",
  "priority": 1,
  "folderRedirection": ["Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos"]
}
```

**SharePoint example:**

```json
{
  "letter": "J",
  "label": "Staff Shared Area",
  "type": "sharepoint",
  "siteUrl": "https://contoso.sharepoint.com/sites/staff",
  "libraryName": "Shared Documents",
  "remotePath": "/",
  "syncMode": "bidirectional",
  "priority": 3
}
```

### `driveFiltering`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | `true` | Probe each configured drive at startup and skip the ones the user has no access to. |
| `checkPermissionsAtStartup` | bool | `true` | Make the access probe at startup vs lazily on first I/O. |
| `skipDriveOn403` | bool | `true` | Treat HTTP 403 from Graph as "user has no permission, skip silently". |
| `logSkippedDrives` | bool | `true` | Log an Information line for each skipped drive (useful for support). |

### `cleanup`

For shared machines (school computer rooms, hot-desk laptops) where a
different user logs in each session.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `cleanOnLogoff` | bool | `true` | Wipe `localStorageRoot` at logoff so the next user starts clean. |
| `cleanOnLogon` | bool | `true` | Wipe `localStorageRoot` at logon as well (defence-in-depth). |
| `cleanupDelayAfterSyncFlushSeconds` | int | `5` | Wait this long after flushing pending uploads before deleting local state. |
| `preserveAuthCache` | bool | `true` | Keep the MSAL token cache so silent SSO works on the next logon. |
| `preserveLogs` | bool | `true` | Don't delete `\Logs\` during cleanup. |
| `preserveQuotaCache` | bool | `true` | Keep the quota response cache to avoid a fresh Graph call at the next logon. |

For single-user laptops, set `cleanOnLogoff` and `cleanOnLogon` both to
`false` to keep files cached across sessions.

### `syncSettings`

Sync engine tuning. Defaults are sensible for an organisation of
1000–2000 users on a normal broadband line. Reduce concurrency on
constrained networks.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `pollIntervalSeconds` | int | `300` | How often OneSync polls Graph for remote changes (delta query cadence). |
| `maxConcurrentUploads` | int | `3` | Parallel upload slots. |
| `maxConcurrentHydrations` | int | `4` | Parallel downloads when files are read from drive letters. |
| `maxConcurrentGraphRequests` | int | `8` | Cap on in-flight Graph calls (protects against rate-limit storms). |
| `shutdownTimeoutSeconds` | int | `90` | Max time to flush pending uploads at logoff before giving up. |
| `maxFileSizeMB` | int | `250` | Files larger than this are not synced — useful to filter accidental VM image / ISO uploads. |
| `excludePatterns` | string[] | see template | Glob patterns excluded from sync (temp files, `~$*`, `Thumbs.db`, etc.). |
| `quotaRefreshIntervalSeconds` | int | `300` | How often the user's storage-quota status is refreshed. |
| `deltaQueryIntervalSeconds` | int | `300` | Delta query interval (the primary mechanism for picking up remote changes). |
| `uploadDebounceMs` | int | `2000` | Wait this long after a local write before queuing an upload (collapses bursts from save-twice tools). |
| `maxRetries` | int | `5` | Per-request retry limit on transient failures. |
| `retryBackoffBaseSeconds` | int | `30` | Initial backoff for retries; exponential with jitter from there. |
| `maxRetryAfterSeconds` | int | `600` | Cap on Graph-supplied `Retry-After` values (so a misbehaving response can't park the worker for hours). |

#### Local-disk eviction

OneSync keeps hydrated copies of files on local disk. To prevent the disk
from filling, it can truncate the least-recently-used hydrated files back
to 0-byte placeholders when free space falls below a threshold. **The
cloud copy is never touched** — eviction is purely local, and the file
re-hydrates on next access.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `evictionEnabled` | bool | `true` | Master switch. Set to `false` to keep every file fully hydrated. |
| `evictionFreeSpaceThresholdGB` | int | `2` | Start evicting when free space on the drive containing `localStorageRoot` drops below this. |
| `evictionTargetFreeSpaceGB` | int | `4` | Stop evicting once free space climbs above this. (Should be > threshold to prevent thrashing.) |
| `evictionCheckIntervalSeconds` | int | `60` | How often the eviction service checks free space. |
| `evictionMinAgeMinutes` | int | `15` | Don't evict files accessed within this window — protects files the user is actively editing. |

### `logging`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `level` | `"Information"` \| `"Debug"` \| `"Warning"` | `"Information"` | Minimum log level. `Debug` is verbose — only enable when reproducing an issue. |
| `path` | path | `%LOCALAPPDATA%\OneSync\Logs` | Log directory. Files are named `onesync-YYYYMMDD.log`. |
| `maxSizeMB` | int | `50` | Per-file size cap before roll. |
| `maxFiles` | int | `10` | Older files beyond this are deleted. |

---

## Deployment

The MSI ships with two PowerShell scripts in `C:\Program Files\OneSync\`:

| Script | Purpose |
|---|---|
| `Install-OneSync.ps1` | Wrapper that runs `msiexec` with sensible defaults, handles Dokan prerequisites, and applies drive-icon registry entries. Accepts `-ConfigFile <path>` to swap in a customised `config.json`. |
| `Uninstall-OneSync.ps1` | Stops `OneSync.exe`, unregisters the shell overlay, runs MSI uninstall, optionally purges per-user state with `-PurgeUserState`. |
| `DriveIcons.ps1` | Module that applies the OneSync drive icon to configured letters and saves the previous icon for restore on uninstall. Called by the install/uninstall scripts. |

For Intune / SCCM / Group Policy rollouts, deploy the MSI directly —
`Install-OneSync.ps1` is convenience for manual installs and lab work.

---

## Logs and troubleshooting

Logs live at `%LOCALAPPDATA%\OneSync\Logs\onesync-YYYYMMDD.log`. The most
useful first commands:

```powershell
# Tail the current log
Get-Content "$env:LOCALAPPDATA\OneSync\Logs\onesync-$(Get-Date -Format yyyyMMdd).log" -Wait -Tail 50

# Is OneSync running?
Get-Process OneSync -ErrorAction SilentlyContinue

# Is the Dokan driver loaded?
Get-Service Dokan2 | Format-List Status, StartType
```

### Common issues

- **Drive letters don't appear in Explorer after install.** OneSync mounts
  drives in the user's session integrity level. If launched from an
  elevated process, the mounted drives are invisible to non-elevated
  Explorer due to UAC token isolation. The MSI launches the binary via
  `explorer.exe` at the end of install to avoid this; if you start it
  manually, do the same: `explorer.exe "C:\Program Files\OneSync\OneSync.exe"`.

- **"Paused syncing" balloon appears frequently.** Graph throttling. Drop
  `maxConcurrentGraphRequests` to `4` and increase `retryBackoffBaseSeconds`
  to `60`.

- **Office shows "couldn't find any locations your org has approved" when
  toggling AutoSave.** The file was opened with a path that didn't route
  through OneSync's launcher. Confirm the per-extension ProgID is the
  default handler for `.docx`/`.xlsx`/`.pptx` in `HKCR`.

- **Local disk fills despite eviction enabled.** Check the log for
  `LruEvictionService` entries. Eviction skips files with pending uploads
  and files accessed within `evictionMinAgeMinutes` — under heavy active
  editing it may not reclaim enough. Reduce `evictionMinAgeMinutes` to
  `5` if needed.

- **`Get-Process OneSync` shows nothing after logon.** Check the public
  Desktop / Common Startup shortcuts exist. If they're missing, the MSI
  install was incomplete; reinstall with
  `msiexec /i OneSync.msi REINSTALLMODE=amus`.

For anything else, set `logging.level` to `"Debug"`, reproduce the issue,
and inspect the log around the timestamp where it occurred.
