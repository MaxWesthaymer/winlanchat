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

The bundled runtime receives updates when a new application installer is published;
rebuild releases with a supported, patched .NET SDK/runtime.
