# AGENTS.md — WinLanChat LAN Messenger

## Project
Build a lightweight Windows LAN messenger using **C#, .NET , WPF**.
The app should have a modern Windows 11-style dark UI, peer discovery, chat, and file transfer. No cloud or central server.

## Architecture
- Follow **MVVM**: Views (XAML), ViewModels, Models, Services. Keep business logic out of code-behind.
- Use dependency injection where useful; avoid unnecessary abstractions and dependencies.
- Use `async`/`await` for I/O; never block the UI thread. Support cancellation for long operations.
- Keep networking, persistence, and UI independent and testable.

## Networking & files
- Discover peers on the local network; handle disconnects and duplicate discovery gracefully.
- Stream files in chunks; never load entire files into RAM. Expose progress, cancellation, and errors.
- Treat LAN peers as untrusted: validate inputs and filenames, prevent path traversal, and require user consent before saving incoming files.
- Keep the app responsive and resource usage low while idle and during transfers.

## UI & code style
- Use WPF bindings, commands, styles, and reusable controls; prefer minimal code-behind.
- Default to a polished dark theme. Keep layouts responsive and use list virtualization for long chats.
- Enable nullable reference types; follow standard .NET naming conventions.
- Make focused changes; avoid unrelated refactoring and speculative features.

## Working rules
- Inspect existing code and conventions before editing; preserve the current architecture.
- After changes, run `dotnet build` and relevant tests on Windows when possible.
- Do not introduce NuGet packages without explaining why they are needed.
- Summarize changes, verification performed, and any remaining limitations.
