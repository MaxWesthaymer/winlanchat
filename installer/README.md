# Windows installer

Starting with **1.0.1**, the installer contains a **self-contained win-x64** Release
publish, including .NET and the WPF/Windows Forms desktop runtime. The user does
not need .NET installed or an internet connection. The runtime stays inside the
application directory; setup does not install or change a system-wide .NET runtime.

## Build

On Windows, use the .NET SDK specified in `global.json` and
[Inno Setup 6.3 or newer](https://jrsoftware.org/isdl.php). Inno Setup is a build
tool only; no NuGet packages are added to the application.

From the repository root:

```powershell
.\scripts\build-installer.ps1
# Optional version and compiler location:
.\scripts\build-installer.ps1 -Version 1.1.0 -InnoSetupCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

Output: `artifacts/installer/WinLanChat-<version>-win-x64-Setup.exe`.
The default version is read from `src/WinLanChat.App/WinLanChat.App.csproj`; an
explicit `-Version` overrides it for that build. Application and setup metadata use
the same version. The script verifies the runtime configuration and required runtime
files before packaging. NuGet restore downloads Microsoft's runtime packs when they
are not already cached; no application package references are added.
Each build publishes to a new directory under `artifacts/publish` to exclude stale
files. These directories may be removed when no longer needed.

## Installation behavior

- Windows 10 22H2 or newer, with x64 application support.
- Installs for the current Windows user, without administrator privileges.
- Offers a destination folder, a Start menu shortcut and an optional desktop shortcut.
- English and Russian setup UI.
- Registers an uninstaller in Windows Settings. Upgrades use the same application ID.
- Asks the user to exit the running messenger (including its tray icon) before
  installing or uninstalling; it does not force-close a conversation or transfer.
- Removes Windows startup registration on uninstall only if it points to this copy.
- Keeps profile settings and received files. Firewall permission is requested by
  Windows when the app first listens on the network, as with a source build.

The generated setup is unsigned. Signing a release requires a code-signing
certificate; no certificate or signing credentials are stored in this repository.

## Publishing updates on GitHub

The updater reads public stable releases from `MaxWesthaymer/winlanchat`.
If releases move to another repository, change `GitHubUpdateService.Repository`
and rebuild the app before distributing it. Never embed a private-repository token
in the application.

1. Set a newer numeric version in `src/WinLanChat.App/WinLanChat.App.csproj`
   (for example `1.0.4`), then run the build and tests below.
2. Run `.\scripts\build-installer.ps1` and test the resulting installer.
3. Create a GitHub Release with the matching tag, such as `v1.0.4`. Include release
   notes and attach `artifacts/installer/WinLanChat-1.0.4-win-x64-Setup.exe`.
   The exact filename and matching tag are required; three- and four-part numeric
   versions are supported, with an optional lowercase `v` in the tag.
4. Publish the release as a stable release and mark it **Latest**. Drafts and
   prereleases are ignored. Upload the installer before publishing the release so
   clients do not see a release with a missing asset.
5. Confirm GitHub's release asset API includes `digest: "sha256:…"`. Without a valid
   GitHub SHA-256 digest, the updater refuses the installer; reupload legacy assets
   that do not have one. No separate checksum file is required.

Users check manually in settings or opt in to one check per launch. There is no
background polling, automatic download or unattended installation. A missing release,
offline connection or GitHub API limit is reported inside the updates section and
does not stop LAN messaging. The updater uses HTTPS and accepts download redirects
only to GitHub's release asset hosts. SHA-256 detects incomplete/corrupted downloads;
it relies on the GitHub repository and HTTPS for trust and does not replace code signing.

The app downloads to a unique directory under `%TEMP%\WinLanChat-Updates`, using a
bounded buffer. Cancellation, failures and unlaunched installers are cleaned up.
A successfully launched setup may remain in that temporary directory while Windows
holds the executable open and can be removed later. Updates are full installers,
including the bundled .NET runtime, rather than differential patches.

The app launches setup with `/UPDATE=1 /WAITFORPID=<app PID> /DIR=<installed path>`.
Setup waits up to 30 seconds for that process to exit before checking the existing
application mutex; it does not force-close it. This mode reopens WinLanChat after a
successful installation. Normal manual installation keeps its optional launch checkbox.
Self-update requires a registered per-user installation whose directory matches the
running app. The app stays open if it cannot launch setup. Cancelling setup after
the app exits leaves the previous version installed; reopen WinLanChat manually.

## Release verification

```powershell
dotnet build src/WinLanChat.App/WinLanChat.App.csproj -c Release
```

When the test project is present, also run
`dotnet run --project tests/WinLanChat.Tests -c Release`.

Test setup and uninstall using a disposable Windows account or VM. Check launch
without a system .NET runtime, installation into a folder with spaces/Unicode,
shortcuts, upgrade from 1.0.0, a running application's tray mutex, and uninstall
cleanup. An existing user's settings and files must survive uninstall.

Also test a real update from an installed 1.0.3 copy to a newer published test release,
including cancellation, a custom install directory, a launch from the tray and the
automatic reopen. The automated checks use fake GitHub HTTP responses and a fake
installer launcher: they never install an update on the developer's machine.

The bundled runtime receives updates when a new application installer is published;
rebuild releases with a supported, patched .NET SDK/runtime.
