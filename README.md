# WinLanChat

A lightweight Windows messenger for chatting and sharing files over your local network. No accounts, cloud, or central server required.

## Features

- Automatically finds other computers running WinLanChat on the same network.
- Direct, one-to-one text chat with clickable links.
- File sharing with drag-and-drop, progress, and cancellation. You choose whether and where to save incoming files.
- Dark interface in English and Russian.
- Background notifications, system tray access, and optional startup with Windows.

## Get started

1. Open WinLanChat on two Windows computers connected to the same local network.
2. If Windows asks for firewall access, allow WinLanChat on your private network.
3. Click a computer under **Network computers** to connect, or select it with the arrow keys and press **Enter**. Connecting from one side is enough.
4. Type a message and press **Enter** to send it. Use **Shift+Enter** for a new line.
5. To share files, drag them into the conversation or click **+**, then press **Send**. The recipient clicks **Save…** to choose a destination.

Open the **☰ menu** to change your display name, language, or listening port in the sliding settings panel. Disconnect before applying network changes. You can also enable **Start with Windows** there. Use the search field above the computer list to filter by name or IP address.

Closing or minimizing the window keeps WinLanChat running in the system tray. Double-click its tray icon to reopen it, or choose **Exit** from the tray menu to quit.

## Connection help

Both computers must have WinLanChat running. If a computer does not appear, click the refresh icon beside **Network computers** or open the **☰ menu** and use **Connect by IP manually** in the settings panel.

Firewalls must allow UDP port **45831** for discovery and TCP port **45832** for chat and files (or your chosen listening port). Guest Wi-Fi, VPNs, and networks that isolate devices may prevent connections.

## Current limitations

- One conversation at a time; chat history is not saved and resets when you reconnect.
- Messages and files are not encrypted, and peer identities are not verified. Use WinLanChat on trusted networks.
- Interrupted file transfers cannot be resumed. Folders are not supported.

## Windows installer

The **1.0.3** installer includes .NET Desktop Runtime for Windows x64;
no separate .NET installation or internet connection is required. Setup includes English/Russian UI,
a destination folder, Start menu and optional desktop shortcuts, and an uninstaller.
See [installer build instructions](installer/README.md) to generate the setup EXE.

## Updates

Open **☰ → Updates** in the settings panel to check GitHub Releases, read the release
notes and download a newer Windows x64 installer. Downloads show progress, support
cancellation and must pass SHA-256 verification before installation. A dot on the
settings button indicates an available update.

Click **Install and restart** and confirm when ready. Finish or cancel file transfers,
handle pending offers and remove unsent attachments first. The installer waits for
WinLanChat to exit, updates the installed copy and reopens it. Settings and saved files
are kept; the current in-memory conversation is lost when the app exits.

**Check for updates when WinLanChat starts** is off by default. Enable it and click
**Apply changes** to save the preference. Update checks need internet access; LAN chat
works independently. No GitHub account or token is required for public releases.

Self-update supports copies installed with the Windows installer. Source builds can
check and download updates, but cannot replace themselves through the installer.
Versions before 1.0.3 need one manual installation to gain the updater.

## Run from source

On Windows, install **.NET SDK 10.0.402** (or a later patch in the 10.0.4xx feature band), then run from the project folder:

```powershell
dotnet run --project src/WinLanChat.App
```
