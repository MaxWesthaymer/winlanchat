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
3. Select a computer under **Network computers** and click **Connect**. Connecting from one side is enough.
4. Type a message and press **Enter** to send it. Use **Shift+Enter** for a new line.
5. To share files, drag them into the conversation or click **+**, then press **Send**. The recipient clicks **Save…** to choose a destination.

Use **Settings** to change your display name, language, or listening port. Disconnect before applying changes. You can also enable **Start with Windows** there.

Closing or minimizing the window keeps WinLanChat running in the system tray. Double-click its tray icon to reopen it, or choose **Exit** from the tray menu to quit.

## Connection help

Both computers must have WinLanChat running. If a computer does not appear, try **Refresh list** or use **Connect by IP manually** in Settings.

Firewalls must allow UDP port **45831** for discovery and TCP port **45832** for chat and files (or your chosen listening port). Guest Wi-Fi, VPNs, and networks that isolate devices may prevent connections.

## Current limitations

- One conversation at a time; chat history is not saved and resets when you reconnect.
- Messages and files are not encrypted, and peer identities are not verified. Use WinLanChat on trusted networks.
- Interrupted file transfers cannot be resumed. Folders are not supported.

## Run from source

On Windows, install **.NET SDK 10.0.402** (or a later patch in the 10.0.4xx feature band), then run from the project folder:

```powershell
dotnet run --project src/WinLanChat.App
```
