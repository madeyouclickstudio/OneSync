# OneSync Deployment Guide

For IT admins deploying OneSync to fleets via Intune, Group Policy, SCCM, or scripted installation. Covers silent/unattended install switches and how to push tenant-specific `config.json` alongside the install.

## TL;DR

For most fleets, one of:

```cmd
:: Bundle (recommended) - one shot, brings .NET runtime + Dokan + OneSync
OneSyncSetup.exe /install /quiet /norestart /log "%TEMP%\onesync-install.log"
```

```cmd
:: Standalone MSI - install Dokan first, then OneSync, then push config.json
msiexec /i Dokan_x64.msi /qn /norestart /L*v "%TEMP%\dokan-install.log"
msiexec /i OneSync.msi /qn /norestart /L*v "%TEMP%\onesync-install.log"
xcopy /Y config.json "C:\Program Files\OneSync\"
```

Three files are published with every release. **`OneSyncSetup.exe` is the path of least resistance** — use it unless your deployment tooling specifically prefers raw MSIs.

| File | What it is | When to pick it |
|---|---|---|
| `OneSyncSetup.exe` | Burn bundle = Dokan + OneSync MSI + .NET runtime | Default. Works for Intune Win32 apps, scripts, manual installs. |
| `OneSync.msi` | OneSync MSI alone (no Dokan, no .NET) | GPO software installation, SCCM MSI-only deployments, layered images. **Install `Dokan_x64.msi` first.** |
| `Dokan_x64.msi` | Dokan 2.x user-mode filesystem driver | Required dependency for the standalone OneSync.msi. The bundle includes it. |

## Bundle (OneSyncSetup.exe) switches

The bundle uses WiX Burn conventions:

| Switch | Effect |
|---|---|
| `/install` | Install (default action) |
| `/uninstall` | Uninstall |
| `/repair` | Repair an existing install |
| `/modify` | Open the modify UI |
| `/quiet` (or `/q`, `/s`) | Fully silent, no UI |
| `/passive` | Progress bar only, no prompts |
| `/norestart` | Suppress reboot |
| `/log "<path>"` | Write a verbose log to this path |

**Example (Intune Win32 app install command):**
```cmd
OneSyncSetup.exe /install /quiet /norestart /log "%PROGRAMDATA%\OneSync\install.log"
```

**Example (Intune uninstall command):**
```cmd
OneSyncSetup.exe /uninstall /quiet /norestart
```

**Detection rule (Intune):** File path `C:\Program Files\OneSync\OneSync.exe` exists, version >= `1.3.2.0`.

## MSI (OneSync.msi) switches

OneSync.msi follows standard `msiexec` conventions:

| Switch | Effect |
|---|---|
| `/i OneSync.msi` | Install |
| `/x OneSync.msi` *or* `/x {ProductCode}` | Uninstall |
| `/qn` | Fully silent, no UI |
| `/qb` | Basic UI (progress bar only) |
| `/qr` | Reduced UI |
| `/qf` | Full UI (default) |
| `/L*v "<path>"` | Verbose log |
| `/norestart` | Suppress reboot |
| `INSTALLFOLDER="<path>"` | Override install location (default: `C:\Program Files\OneSync`) |
| `REINSTALL=ALL REINSTALLMODE=vomus` | Repair / reinstall on top |

**Example (silent install to custom location):**
```cmd
msiexec /i OneSync.msi /qn /norestart INSTALLFOLDER="D:\Apps\OneSync" /L*v "%TEMP%\onesync.log"
```

**Note:** OneSync.msi does **not** ship the Dokan driver. Install `Dokan_x64.msi` first:
```cmd
msiexec /i Dokan_x64.msi /qn /norestart
```

## Pushing config.json

OneSync looks for `config.json` in three locations, in priority order:

| Path | Purpose |
|---|---|
| `%LOCALAPPDATA%\OneSync\config.json` | Per-user — written by the first-run setup wizard. Wins over machine-wide. |
| `%PROGRAMDATA%\OneSync\config.json` | **Machine-wide — recommended for IT deployment.** |
| `C:\Program Files\OneSync\config.json` | Installer placeholder (all-zero GUIDs until you replace it). |

For fleet deployment, drop your tenant `config.json` at `%PROGRAMDATA%\OneSync\config.json`. It's readable by all users, writable by admins, and OneSync picks it up before falling through to the installer placeholder — so the first-run wizard never fires on a managed machine. Individual users can still create a per-user override at `%LOCALAPPDATA%\OneSync\config.json` if they need a different drive set.

**Three common patterns:**

### 1. Copy to %PROGRAMDATA% (recommended)

```cmd
:: After OneSync.msi or OneSyncSetup.exe runs
md "%PROGRAMDATA%\OneSync" 2>nul
xcopy /Y "\\fileserver\OneSync\config.json" "%PROGRAMDATA%\OneSync\"
```

No admin file overwrite of `Program Files`, no UAC prompts on individual user logons, no risk of a future installer upgrade re-stamping the placeholder over your config.

### 2. Intune Win32 app with bundled config

Package `OneSyncSetup.exe` + your tenant `config.json` together. Win32 app install command:
```cmd
cmd.exe /c "OneSyncSetup.exe /install /quiet /norestart && (md ""%PROGRAMDATA%\OneSync"" 2>nul) && copy /Y config.json ""%PROGRAMDATA%\OneSync\config.json"""
```

### 3. Group Policy Preferences file copy

Deploy `OneSync.msi` via *Software Installation* GPO, then use *Computer Configuration → Preferences → Windows Settings → Files* to copy `config.json` to `%PROGRAMDATA%\OneSync\` on next logon. (Create the folder via *Preferences → Windows Settings → Folders* if it doesn't exist.)

### Alternative: overwrite the installer placeholder

If you'd rather drop the config straight into `C:\Program Files\OneSync\`, that works too — the placeholder lives there ready to be overwritten. Requires admin write access, and a future major-version reinstall could restore the placeholder. Use the `%PROGRAMDATA%` path above instead unless you have a specific reason.

## Office file associations (per-user, one-time)

OneSync registers itself as the default app for `.docx` / `.xlsx` / `.pptx` at machine scope (HKCR) so that double-clicking an Office file inside a OneSync drive routes through OneSync's file handler — which in turn launches Word / Excel / PowerPoint with the file's SharePoint URL, lighting up native AutoSave and real-time co-authoring.

**Fresh Windows profile that has never opened Office files:** the machine-wide registration wins. No user action required — double-click just works and AutoSave engages.

**Existing profile where Word / Excel / PowerPoint have previously been opened:** Windows stores a per-user `UserChoice` key under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.docx\UserChoice` that overrides the machine-wide default. Office's installer typically sets this on first run. Windows 10/11 blocks apps from writing `UserChoice` programmatically (anti-hijack protection — the key has a per-user hash that only Explorer can compute), so the OneSync installer **cannot** fix this for you. The user has to retarget the extensions once.

Recommended rollout-email blurb to include with your deployment:

> **Enabling AutoSave on your H:/I:/J: drives (one-off step)**
>
> The first time you open an Office file on a OneSync drive, you may notice AutoSave isn't switched on. To fix it:
>
> 1. Right-click any Word document inside H: (or whichever drive you use)
> 2. Choose **Open with** → **Choose another app**
> 3. Pick **OneSync** from the list
> 4. Tick **"Always use this app to open .docx files"**
> 5. Click **OK**
>
> Repeat once for an Excel file (`.xlsx`) and once for a PowerPoint file (`.pptx`). After that, every Office file on your OneSync drives opens with AutoSave and co-authoring enabled automatically.

A future OneSync release may detect the shadowed `UserChoice` on first launch and pop a one-time helper dialog, but for now this needs to be a human step.

## Tenant prep checklist

Before deploying to users, ensure you have an Entra ID app registration set up.

> **Important: OneSync does NOT use a client secret.** It runs as a **public client** (desktop app) using MSAL's WAM broker for SSO. The signed-in Windows user authenticates with their own delegated permissions; there's no application secret stored in `config.json`, on the endpoint, or anywhere else. This is deliberate — distributing and rotating shared secrets to every endpoint is exactly the operational pain OneSync is designed to avoid.
>
> **Do not create a client secret or certificate on the OneSync app registration.** OneSync won't use it, and an unused secret on a public-client app is just an audit-finding waiting to happen.

Steps (sign in to https://entra.microsoft.com with a Global Administrator or Application Administrator account):

### 1. Create the app registration

- Left nav → **Identity** → **Applications** → **App registrations**
- Click **+ New registration** at the top
- **Name:** `OneSync` (or whatever you prefer — users won't see this)
- **Supported account types:** select **Accounts in this organisational directory only (Single tenant)**
- **Redirect URI:** leave the dropdown blank for now — we'll add the right one in step 3
- Click **Register**

You'll land on the Overview page. Note the **Application (client) ID** and **Directory (tenant) ID** — you'll paste these into `config.json` at the end.

### 2. Allow public client flows

- Still inside your new app registration, left nav → **Authentication**
- Scroll down to **Advanced settings** → **Allow public client flows**
- Toggle to **Yes**
- Click **Save** at the top

This is what tells Entra that OneSync is a desktop app (no client secret) rather than a web app (needs a secret). **If you skip this step, sign-in will fail with `AADSTS7000218`.**

### 3. Add the desktop-app platform + redirect URIs

Still on the **Authentication** page:

- Under **Platform configurations**, click **+ Add a platform**
- A right-hand panel appears with three tile choices: **Web**, **Single-page application**, **Mobile and desktop applications**
- Pick **Mobile and desktop applications** (NOT Web — that one expects a client secret and is for server-side apps)
- On the next panel:
  - Tick the box next to `https://login.microsoftonline.com/common/oauth2/nativeclient`
  - Also tick the suggested URI `https://login.live.com/oauth20_desktop.srf` if shown
  - In the **Custom redirect URIs** box, add **two more**:
    - `http://localhost`  (used by OneSync's MSAL builder as the fallback browser redirect)
    - `ms-appx-web://microsoft.aad.brokerplugin/<APPLICATION-CLIENT-ID>`  (the WAM-broker redirect — substitute the Application (client) ID you noted in step 1 for the `<APPLICATION-CLIENT-ID>` placeholder)
- Click **Configure** at the bottom

The WAM-broker URI is what enables silent SSO on domain-joined / Entra-joined Windows machines — without it, MSAL falls back to a browser sign-in popup on every launch.

### 4. Add the Graph permissions

Left nav → **API permissions**:

- Click **+ Add a permission**
- Pick **Microsoft Graph**
- Pick **Delegated permissions** (NOT Application permissions — OneSync acts as the signed-in user, not as itself)
- In the search/filter box, find and tick each of these in turn:
  - `Files.ReadWrite` (under "Files")
  - `Sites.ReadWrite.All` (under "Sites")
  - `offline_access` (under "OpenId permissions")
  - `User.Read` (under "User")
- Click **Add permissions**

### 5. Grant admin consent

- Still on **API permissions**, click **Grant admin consent for `<your tenant>`** (the button across the top of the permission list)
- Confirm — every permission should show a green tick under "Status"

Without this, users will see a "needs admin approval" consent prompt the first time they sign in.

### 6. Don't add a client secret

- Left nav → **Certificates & secrets** — **don't touch this page.** No secret, no certificate, no federated credential. OneSync is a public client and won't use any of them. Leaving this empty is correct.

### 7. Paste the IDs into `config.json`

Back on the **Overview** page, copy the two GUIDs and paste them into `config.json`:

```json
{
  "tenantId":  "<Directory (tenant) ID>",
  "clientId":  "<Application (client) ID>",
  "authority": "https://login.microsoftonline.com/<Directory (tenant) ID>"
}
```

That's it. The first time a user runs OneSync, MSAL silently acquires a token via the WAM broker (no popup on domain-joined or Entra-joined machines) and caches it in `%LOCALAPPDATA%\OneSync\` encrypted with the user's DPAPI key.

## Update / upgrade

OneSync uses MSI MajorUpgrade — installing a newer version automatically removes the older one. Same command as a fresh install:
```cmd
OneSyncSetup.exe /install /quiet /norestart
```

The user's local hydrated files, pending uploads, and configuration are preserved across upgrades.

## Troubleshooting silent installs

- **Install returns exit 1603**: usually a custom action failed. Check the verbose log (`/L*v`). The most common cause on managed machines is restrictive ACLs on shortcut files — 1.3.1+ includes a defensive CA that handles this, but if you're hitting it on older builds, manually delete `%PUBLIC%\Desktop\OneSync.lnk` before uninstall.
- **Install returns exit 1638**: another version is already installed. Use the bundle's `/install` (it handles upgrades) or uninstall the old version first.
- **Drives don't mount**: check `%LOCALAPPDATA%\OneSync\Logs\*.log` for `Dokan driver is not installed` — usually means the Dokan MSI didn't run. Reinstall via `msiexec /i Dokan_x64.msi /qn`.

## Removal

```cmd
:: Bundle
OneSyncSetup.exe /uninstall /quiet /norestart

:: Or standalone MSI
msiexec /x OneSync.msi /qn /norestart
:: (optional) remove Dokan too if no other software uses it
msiexec /x Dokan_x64.msi /qn /norestart
```

The uninstaller removes the application binaries but leaves `%LOCALAPPDATA%\OneSync\` (user state, logs, pending uploads) intact. To fully purge user state too, delete that folder after uninstall.

## Questions

Open an issue at https://github.com/madeyouclickstudio/OneSync.
