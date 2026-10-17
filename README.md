# WinLanChat

Windows LAN messenger built with C#, .NET 10 and WPF. No cloud or central server.

## Current milestone

- Dark WPF interface with MVVM and a virtualized conversation list.
- One direct TCP conversation at a time, connected manually by IP address or hostname.
- Text messages in both directions, Unicode and multiline text, up to 8,000 characters.
- Handshake validation, bounded protocol frames, connection timeout and cancellation.
- Disconnect/reconnect and graceful shutdown.
- No third-party NuGet packages.

Automatic peer discovery and file transfer are **not implemented yet**. Message history is in memory only; a new connection starts a new conversation, and only the most recent 2,000 messages are retained. The current protocol has no encryption or peer authentication; displayed names are supplied by the remote computer.

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
2. Enter a name and press **Начать работу** on each computer.
3. If Windows asks about firewall access, allow the application on the private network.
4. On one computer, enter the other computer's displayed IPv4 address and listening port (default: 45832), then press **Подключиться**. Connecting from one side is sufficient.
5. Type a message and press **Отправить** or **Ctrl+Enter**. Enter inserts a newline.

To test two instances on one computer, set their listening ports to 45832 and 45833. Connect one to 127.0.0.1 and the other instance's port.

## Verification

The dependency-free console runner executes real loopback networking checks and renders the WPF window on a dedicated STA thread. It returns a nonzero exit code on failure:

~~~powershell
dotnet run --project tests/WinLanChat.Tests
~~~

Coverage: Unicode and maximum-size messages, concurrent sends, validation, busy-peer rejection, reconnect, handshake cancellation, malformed frames and port release on shutdown, and WPF layout loading. This runner is invoked with dotnet run, not dotnet test.

To save render previews while running the checks:

~~~powershell
dotnet run --project tests/WinLanChat.Tests -- --preview artifacts/preview
~~~

## Layout

- src/WinLanChat.App — views, styles, view models and UI behavior.
- src/WinLanChat.Core — independent protocol, TCP service and models.
- tests/WinLanChat.Tests — executable networking checks.

## Next milestones

1. UDP discovery with stable peer identifiers, expiry and duplicate handling.
2. File offers with explicit acceptance and destination selection; safe filenames, chunked transfer, progress, cancellation and incomplete-file cleanup.
3. Local history, notifications and packaging.
