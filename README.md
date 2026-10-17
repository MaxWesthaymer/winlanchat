# WinLanChat

Windows LAN messenger built with C#, .NET 10 and WPF. No cloud or central server.

## Current milestone

- Dark WPF interface with MVVM and a virtualized conversation list.
- Automatic IPv4 LAN discovery with a computer list, selection and a refresh button.
- Receiving messages and discovering computers start automatically when the application opens.
- Name and listening port can be changed in **Настройки** and applied without restarting the app. Applied settings are saved locally for the next launch.
- One direct TCP conversation at a time; manual IP/hostname connection is available in the expandable settings.
- Text messages in both directions, Unicode and multiline text, up to 8,000 characters.
- Handshake validation, bounded protocol frames, connection timeout and cancellation.
- Disconnect/reconnect and graceful shutdown.
- No third-party NuGet packages.

File transfer is **not implemented yet**. Message history is in memory only; a new connection starts a new conversation, and only the most recent 2,000 messages are retained. The current protocol has no encryption or peer authentication; displayed names are supplied by the remote computer.

## Requirements

Windows and .NET SDK 10.0.402 (or a later patch in the same feature band).
Visual Studio is optional.

## Build and run

~~~powershell
dotnet build WinLanChat.sln
dotnet run --project src/WinLanChat.App
~~~

## Try it on two computers

1. Run the application on both computers in the same LAN.
2. Receiving messages and searching for computers start automatically, using your saved settings (your Windows user name and TCP port 45832 on first launch).
3. If Windows asks about firewall access, allow the application on the private network.
4. Computers running WinLanChat appear under **КОМПЬЮТЕРЫ В СЕТИ**. Select one and press **Подключиться**. Connecting from one side is sufficient. Press **Обновить список** to search immediately.
5. Type a message and press **Отправить** or **Enter**. **Shift+Enter** inserts a newline.
6. Select text within a message and press **Ctrl+C** to copy it, or right-click and choose **Копировать**. **Ctrl+A** selects the entire message when its text has focus.
7. Click an **http://**, **https://** or **www.** link in a message to open it in your default browser. Addresses beginning with **www.** open over HTTPS.

Open **Настройки** to edit your name or listening port, then press **Применить изменения**. Finish the current connection before applying; editing a draft does not affect the active listener or discovery announcements. **Отменить изменения** restores the applied values. If a new port cannot be opened, the app attempts to restore the previous listener and does not save the failed settings. Settings are saved in `%LOCALAPPDATA%/WinLanChat/settings.json`; an unreadable or invalid file is reported and the app starts with defaults.

If the listening port is occupied at startup, settings open automatically. Choose a free port and press **Применить изменения**. To test two instances on one computer, use port 45832 for the first and change the second to 45833 after the port conflict. Both instances share the same per-user settings file; the last successfully applied settings are used on the next launch. They share the discovery port and appear separately in the list, even with identical names. Manual connection is also available under **Подключение по IP вручную**.

## Peer discovery

Discovery uses application-specific UDP broadcasts on port **45831**; chat uses TCP **45832** by default (or the configured listening port). Allow inbound UDP 45831 and the selected TCP port for this app on the Windows private network profile. No firewall settings are changed automatically.

Only computers with WinLanChat running and receiving connections are listed. This is not a scan of all Windows computers and does not implement IP Messenger's wire protocol. Peers announce every 10 seconds; silent peers expire after about 35 seconds. Normal shutdown sends a departure announcement. Refresh sends an immediate discovery query without interrupting the current conversation.

Each running instance has a session identifier, so repeated announcements do not create duplicate rows and identical user/computer names remain separate. Discovery validates bounded packets, protocol versions, names and TCP ports; the connection address is taken from the UDP sender, not an advertised host field. Up to 1,024 participants are kept in memory. Peer names and presence are untrusted and unauthenticated.

Broadcasts target active IPv4 adapter subnets and loopback for local testing. Discovery is limited to the local broadcast network: routers, guest Wi-Fi isolation, VPNs or firewall rules may block it. IPv6 discovery and discovery across routed subnets are not implemented; use the manual connection option where needed.

## Verification

The dependency-free console runner executes real loopback networking checks and renders the WPF window on a dedicated STA thread. It returns a nonzero exit code on failure:

~~~powershell
dotnet run --project tests/WinLanChat.Tests
~~~

Coverage: Unicode and maximum-size messages, concurrent sends, validation, busy-peer rejection, reconnect, handshake cancellation, malformed frames, port release on shutdown, discovery and connection through discovered endpoints, shared-port broadcasts, automatic refresh, duplicate/self suppression, departure/expiry, invalid discovery packets, refresh cancellation, automatic app startup, applying names and ports, unchanged live identity while editing, port-conflict recovery, saved settings, corrupt settings, cancelled writes, save retries, and WPF layout loading. This runner is invoked with dotnet run, not dotnet test.

To save render previews while running the checks:

~~~powershell
dotnet run --project tests/WinLanChat.Tests -- --preview artifacts/preview
~~~

## Layout

- src/WinLanChat.App — views, styles, view models and UI behavior.
- src/WinLanChat.Core — independent protocol, TCP service and models.
- tests/WinLanChat.Tests — executable networking checks.

## Next milestones

1. File offers with explicit acceptance and destination selection; safe filenames, chunked transfer, progress, cancellation and incomplete-file cleanup.
2. Local history, notifications and packaging.
