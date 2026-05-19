# OneSync

A lightweight Windows drive-letter sync client for OneDrive and SharePoint, built for organisations (schools, in particular) that want file-server-style UX on top of Microsoft 365 without running the Microsoft OneDrive sync client — and for those who don't want to pay for Cloud Drive Mapper, don't need an enterprise application solution, and want the features we've added on top (accurate folder sizes in Explorer, working image thumbnail previews, a live upload/sync widget, an in-app Recycle Bin viewer, and more).

OneSync mounts OneDrive and SharePoint libraries as native Windows drive letters (e.g. `H:` for personal OneDrive, `I:` and `J:` for shared SharePoint libraries). Files appear instantly as placeholders; the bytes are fetched only when a user opens them. Folder contents load on demand the first time a user navigates into them, so first-mount is cheap regardless of how big the underlying library is.

**Built to avoid Graph throttling.** OneSync is deliberately conservative with Microsoft Graph requests so that estate-wide deployments don't trip tenant-level rate limits:
- **Token-bucket client-side rate limiting** caps each machine at 30 requests/second sustained, 60 burst (tunable). One runaway user can't starve the rest of the tenant.
- **`?token=latest` bootstrap** skips the initial `/delta` walk that other sync clients use — first mount on a fresh machine fetches zero items, and content only appears as the user actually navigates into folders.
- **Lazy folder enumeration** means a SharePoint library with 500k items costs *nothing* until a user clicks into it; only the folders they actually open hit Graph.
- **Cooldown awareness** — if Graph does push back with a 429 / `Retry-After`, OneSync respects the backoff and pauses every drive on that machine until the cooldown lifts, surfaced in logs as structured events so IT can spot patterns.
- **No initial-storm at logon.** 200 students logging in at 08:55 generates ~600 tiny token-establishment calls in 15 minutes (well under 1 req/s estate-wide), not the thousands-per-second storm a full enumeration would produce.

## Download

Latest installer: see the [Releases page](../../releases). Three files are published with each release:

- **`OneSyncSetup.exe`** — most users start here. One-shot bundle that brings the .NET 8 runtime, Dokan 2.x driver, and OneSync MSI.
- **`OneSync.msi`** — for IT admins deploying via Intune / GPO / SCCM as a raw MSI. Install `Dokan_x64.msi` first.
- **`Dokan_x64.msi`** — the Dokan user-mode filesystem driver. Required dependency for the standalone MSI; included in the bundle.

Requires local administrator rights (the Dokan filesystem driver is kernel-mode).

For unattended / fleet deployment with silent install switches and tenant configuration, see [DEPLOYMENT.md](DEPLOYMENT.md).

## Inspiration & credits

OneSync wouldn't exist without two commercial tools that pioneered the drive-letter-for-cloud-storage approach:

- **[Cloud Drive Mapper](https://www.iamcloud.com/cloud-drive-mapper-overview/)** by IAMCloud — the gold standard in this space, particularly for large enterprise and multi-site education estates. If you're running thousands of seats across multiple tenants with complex governance requirements, their product is the right fit. Their work proved the model and shaped a lot of the UX expectations users now have.
- **[ZeeDrive](https://www.zeedrive.com/)** — another well-built take on the same idea, lighter touch and great for smaller deployments.

OneSync is a free alternative aimed at the gap between "I want this UX" and "I can justify a commercial licence." It's the right pick when your org is too small or budget-constrained for a paid product, but the OneDrive sync client is the wrong shape for your users.

## What OneSync adds on top

Most of these came from running similar tools in production at a single school and finding rough edges that mattered to end users. They're not criticisms of the commercial products — they're problems we hit and chose to spend time on:

- **Accurate free-space in File Explorer.** Right-clicking a drive shows the real OneDrive / SharePoint quota and remaining bytes (refreshed periodically via Graph), not a synthetic placeholder. Users actually know when they're about to run out.
- **Image thumbnail previews.** JPG / PNG / HEIC thumbnails render in Explorer the same way they do on a local disk, without the user having to open the file first.
- **Live upload / sync status widget.** A floating tray modal shows in-progress uploads, deletes, and conflict resolutions with percentage and filename, so users can tell at a glance whether their save has actually made it to the cloud.
- **Recycle bin access.** The tray's Recycle Bin view shows recently-deleted files from this client for visibility — clicking Restore on anything (recent or older) opens the OneDrive / SharePoint recycle bin in your browser, where the actual restore happens.
- **Recycle-bin desktop icon stays clean.** The OneDrive Recycle Bin folder is excluded from the delta sync, so the local desktop Recycle Bin icon never flips to the "items present" state because of cloud-side deletes the user doesn't care about.
- **Cross-machine pending-upload notification.** If a staff member leaves work-in-progress on one PC and signs into another, a tray balloon on the new machine tells them which files are still pending on which device.

## Why OneSync instead of OneDrive sync client?

- **Drive letters, not user-profile folders.** Network-drive style (`H:`, `I:`, `J:`) matches the mental model schools have used for decades. No retraining users to navigate `%USERPROFILE%\OneDrive — Tenant`.
- **Multi-library out of the box.** Mount any number of SharePoint document libraries alongside OneDrive without per-user sync-pair fiddling.
- **Bounded local footprint.** Session-cache mode wipes the placeholder metadata on graceful shutdown so persistent staff profiles never accumulate gigabytes of sync state. Hydrated file content and pending uploads survive the wipe.
- **Lazy-by-default.** No initial folder-walk storm at mount. Folder enumerations happen only when the user opens that folder. First-time login on a fresh machine costs a handful of small Graph requests, not thousands.
- **Office co-authoring.** Double-clicking an Office document opens it directly via the SharePoint URL, so AutoSave and real-time co-authoring work the same as opening from the web. (On existing user profiles where Word / Excel / PowerPoint are already the default app, you may need to set OneSync as the default for `.docx` / `.xlsx` / `.pptx` once — see the FAQ.)

## What it is not

- Not an enterprise-grade replacement for OneDrive sync at FAANG-scale tenant counts. Intended for a single school or small organisation (low hundreds of concurrent users). For larger or more complex deployments, look at Cloud Drive Mapper.
- Not for offline-first workflows where users routinely lose network connectivity. OneSync assumes connectivity is the normal state; offline edits queue and resync when connectivity returns, but the UX is optimised for online use.
- Not a Microsoft product, not endorsed by Microsoft. Uses the documented Microsoft Graph API.

## Requirements

- Windows 10 (1809) or Windows 11
- Local administrator rights to install (kernel-mode Dokan driver)
- A Microsoft 365 tenant with at least one Entra app registration (see Configuration below)

## Configuration

**First launch shows a setup wizard.** It asks for Tenant ID, Application (client) ID, and your drives (letter, label, OneDrive vs SharePoint, site URL for SharePoint), then writes the result to `%LOCALAPPDATA%\OneSync\config.json` — no admin rights required. OneSync then continues into normal startup.

> **Heads up: shell folder redirection is on by default.** For each OneDrive drive, the wizard sets `folderRedirection: ["Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos"]`, which relocates your Windows shell folders onto that drive (so Documents lives at e.g. `H:\Documents`). This matches the "everything goes in OneDrive" pattern most school / small-org deployments want. **If you don't want it**, after first launch edit `%LOCALAPPDATA%\OneSync\config.json`, find the drive entry, and either remove the `folderRedirection` array or set it to `[]`, then restart OneSync.

For advanced settings — folder redirection, sync tuning, exclude patterns, log levels, eviction thresholds — edit `%LOCALAPPDATA%\OneSync\config.json` directly after first launch. Every available setting is documented in the [`config.template.json`](config.template.json) reference. The JSON loader supports `//` comments and trailing commas, so you can copy snippets from the template if you want to override a default.

Minimum viable config (about 15 lines, the shape the wizard produces):

```json
{
  "tenantId":  "your-tenant-guid",
  "clientId":  "your-app-registration-guid",
  "authority": "https://login.microsoftonline.com/your-tenant-guid",
  "drives": [
    { "letter": "H", "label": "Home", "type": "onedrive", "remotePath": "/" }
  ]
}
```

The Entra app registration needs delegated permissions: `Files.ReadWrite`, `Sites.ReadWrite.All`, `offline_access`, `User.Read`, and must allow public client flows.

**No client secret required.** OneSync runs as a public client (desktop app) using MSAL's WAM broker — the signed-in Windows user authenticates with their own delegated permissions, so there's no shared application secret to distribute to endpoints, store in config, or rotate. Don't create a client secret on the app registration; OneSync won't use it. Full setup steps in [DEPLOYMENT.md](DEPLOYMENT.md#tenant-prep-checklist).

For IT fleet deployment, drop a tenant-wide config at `%PROGRAMDATA%\OneSync\config.json` — see [DEPLOYMENT.md](DEPLOYMENT.md#pushing-configjson). OneSync prefers that path over the installer's placeholder, so a fresh user on a managed machine never sees the wizard.

## Security

- All traffic goes over HTTPS to `graph.microsoft.com`.
- MSAL token cache is per-user, encrypted at rest via Windows DPAPI.
- No telemetry. OneSync makes no outbound network requests other than to Microsoft Graph using the signed-in user's delegated permissions.
- Found a security issue? See [SECURITY.md](SECURITY.md).

## FAQ

**Do I need to create a client secret in the Entra app registration?**
No. OneSync is a **public client** (desktop app) using delegated permissions — the signed-in Windows user is the credential. Public clients can't store secrets securely, which is why the protocol doesn't use one. Most apps that talk to Graph from a *server* need a secret because they act as themselves; OneSync runs on the user's PC and acts as the user, which is a different auth pattern. Don't add a secret to the app registration — OneSync won't use it and it's a needless audit liability. Full step-by-step app-registration walkthrough is in [DEPLOYMENT.md](DEPLOYMENT.md#tenant-prep-checklist).

**Does OneSync redirect my Documents / Desktop folders by default?**
Yes. The setup wizard enables shell folder redirection (`Desktop`, `Documents`, `Downloads`, `Music`, `Pictures`, `Videos`) for OneDrive drives, so those Windows shell folders point at the OneSync drive (e.g. Documents lives at `H:\Documents`). It matches the "everything goes in OneDrive" pattern school deployments coming from a file-server world expect. To opt out, edit `%LOCALAPPDATA%\OneSync\config.json` after first launch — find your OneDrive entry and either remove the `folderRedirection` array or set it to `[]`, then restart OneSync.

**Office AutoSave / co-authoring isn't kicking in for my users — what gives?**
Co-authoring and AutoSave only engage when an Office document opens via OneSync's file handler (which redirects it to the SharePoint URL). On a fresh Windows profile that's automatic — the installer registers OneSync as the default app for `.docx` / `.xlsx` / `.pptx` machine-wide. But on profiles where Word / Excel / PowerPoint have *already* been opened, Windows has stored a per-user "UserChoice" pointing at Office, and that overrides OneSync's machine-wide registration. (This is anti-hijack protection in Windows 10/11 — apps can't programmatically retarget the default for you.) Fix it once per user: right-click any Word document on a OneSync drive → **Open with** → **Choose another app** → pick **OneSync**, tick **"Always use this app to open .docx files"**, click **OK**. Repeat for an Excel and a PowerPoint file. After that, double-click works as documented and AutoSave engages on every subsequent open.

**Why not just use the Microsoft OneDrive sync client?**
Three things, in order of how much they bite at scale:

1. **Local disk footprint.** OneDrive sync (even with Files On-Demand) accumulates state on the user's machine that doesn't go away on its own — hydrated files stay hydrated, the placeholder tree mirrors the full cloud structure, and various index / state files (`.OneDriveSync.dat`, group policy caches, the `OneDriveTemp` folder) grow over time. On persistent staff profiles this routinely turns into multi-gigabyte local sprawl that IT has to manually clear. OneSync's session-cache mode wipes the placeholder metadata and cache on every clean logout, so each new session starts at near-zero local footprint. Hydrated files and pending uploads survive — anything you actually opened or saved stays on disk — but the bulk junk gets cleared.
2. **Drive letters, not user-profile folders.** OneDrive sync puts your files in `%USERPROFILE%\OneDrive — TenantName`. For schools coming from a file-server world where `H:`, `I:`, and `J:` mean specific things to every user, the mental-model mismatch is significant. OneSync mounts each library as a real drive letter, which Just Works for users with no retraining.
3. **Multi-library out of the box.** OneDrive sync doesn't expose shared SharePoint libraries as drive letters — you can sync individual libraries into folders, but each one is its own per-user setup that someone (the user, or IT via policy) has to configure. OneSync mounts any number of OneDrive + SharePoint libraries from a single central `config.json`.

**Does OneSync send my files or any data anywhere besides Microsoft Graph?**
No. There is no telemetry, no analytics, no crash reporting to anyone but the local crash log on the user's own machine. The only outbound network calls OneSync makes are to `graph.microsoft.com` and `login.microsoftonline.com`, both Microsoft endpoints, using the signed-in user's own delegated permissions.

**Can I use OneSync on shared / multi-user machines (e.g. student lab PCs)?**
Yes. Each Windows user account gets its own per-user state in `%LOCALAPPDATA%\OneSync\` — separate token cache, separate metadata cache, separate placeholder tree. Two students signing into the same machine see only their own OneDrive / SharePoint content.

**What happens to my files if I uninstall OneSync?**
The cloud copy is untouched — OneSync never deletes anything from OneDrive or SharePoint on uninstall. Locally, hydrated files (anything you actually opened or saved) remain in `%LOCALAPPDATA%\OneSync\Drives\` so you don't lose offline work. Placeholders for files you never opened are removed because they were just pointers, not real content.

**Will hydrated files eventually clog up the user's local disk?**
No. OneSync has a background LRU eviction service. With defaults:
- When local free space drops below **2 GB**, OneSync starts truncating the least-recently-used hydrated files back to 0-byte placeholders, until **4 GB** is free again.
- Files accessed in the last **15 minutes** are exempt (so it never yanks a file you're actively editing).
- The cloud copy is never touched — only the local cached bytes. Next time the user opens an evicted file, OneSync re-hydrates it transparently.
- All thresholds are tunable in `config.json` (`evictionEnabled`, `evictionFreeSpaceThresholdGB`, `evictionTargetFreeSpaceGB`, `evictionMinAgeMinutes`).

Without disk pressure, hydrated files stay forever — but growth is naturally bounded by what the user actually opens. Unlike OneDrive sync (which can pre-hydrate based on policy or "Always Keep on Device"), OneSync only ever puts bytes on disk when the user explicitly opens a file, so a typical staff member churns through a few hundred MB at most and the LRU never needs to fire.

**Does this work behind a corporate proxy / TLS-inspecting firewall?**
Yes, as long as the user's Windows account can reach `graph.microsoft.com` and `login.microsoftonline.com`. OneSync uses .NET's `HttpClient` which honours the system proxy settings (the same ones Edge / Office use). TLS inspection works fine provided the inspecting cert is trusted by the Windows cert store.

**How do I update to a newer version?**
Just run the new `OneSyncSetup.exe` — it does an MSI MajorUpgrade in place. User configuration, hydrated files, and pending uploads survive the upgrade.

## Repository structure

| Path | What's there |
|---|---|
| `src/OneSync/` | Main app — .NET 8 (WinForms + WPF), Dokan filesystem layer, Microsoft Graph gateway, setup wizard, tray + widgets |
| `src/OneSync.ShellOverlay/` | C++ shell extension DLL: drive icon overlays + Explorer image thumbnail provider |
| `src/OneSync.Installer/` | WiX 6 MSI definition and Burn bootstrapper bundle |
| `config.template.json` | Annotated reference of every available config setting |
| `DEPLOYMENT.md` | IT deployment guide (Intune / SCCM / GPO / `%PROGRAMDATA%` config push) |

## Building from source

Requires the .NET 8 SDK and the WiX 6 CLI (`dotnet tool install --global wix --version 6.*`). The C++ ShellOverlay project builds separately (Visual Studio with the C++ workload): build it once and drop the resulting `OneSyncShellOverlay.dll` into `src/OneSync.Installer/stage/`, then run:

```powershell
.\src\OneSync.Installer\build.ps1
```

That produces `OneSync.msi` next to the script and the `OneSyncSetup.exe` bundle in `dist/`. The bundle also expects `Dokan_x64.msi` at `dist/Dokan_x64.msi` — grab it from the [Dokan releases page](https://github.com/dokan-dev/dokany/releases) once.

## License

[MIT](LICENSE) — free to use, modify, and redistribute, including commercially. Keep the copyright notice. Source is in this repo; pull requests welcome.
