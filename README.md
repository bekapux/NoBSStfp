# NoBSSftp

A no-nonsense, tabbed SFTP client built with Avalonia UI and .NET 10. It includes a file browser with drag-and-drop transfers plus quick launch into your system SSH terminal.

## Features
- Multi-tab SFTP sessions
- Server library with folders, drag-and-drop ordering, and quick connect
- File browser with CRUD, context menus, and keyboard shortcuts
- External file drag-and-drop upload and internal drag-move
- Centered connecting/loading indicator while establishing SFTP sessions
- External SSH terminal launch from the active session
- Secure credential persistence via OS keychain (macOS)
- Verified quick-connect on saved servers via macOS LocalAuthentication (Touch ID / device auth)

## Tech Stack
- .NET 10 (TargetFramework: net10.0)
- Avalonia UI 11.3.x (MVVM)
- SSH.NET for SFTP/SSH
- CommunityToolkit.Mvvm for MVVM helpers
- Material.Icons.Avalonia

## Project Layout
```
NoBSStfp/src
  Models/
  Services/
  ViewModels/
  Views/
  Assets/
```

## Getting Started

### Prerequisites
- .NET 10 SDK (preview/RC as required by `net10.0`)

### Build
```
dotnet restore NoBSStfp/src/NoBSSftp.csproj
dotnet build NoBSStfp/src/NoBSSftp.csproj
```

### Run
```
dotnet run --project NoBSStfp/src/NoBSSftp.csproj
```

### Publish (AOT, macOS ARM64)
```
dotnet publish NoBSStfp/src/NoBSSftp.csproj -c Release -r osx-arm64
```

## Notes
- AOT + trimming is enabled; JSON serialization uses source generation for compatibility.
- Terminal access is provided via your OS terminal app (`ssh`) from inside the session UI.
- Saved profile credentials are persisted in macOS Keychain via native Security.framework calls and stripped from `servers.json`.
- Reusing saved secrets from the connect dialog requires macOS device-owner verification (Touch ID/password).
- Keychain secrets are fetched on-demand during connect flows (not at app startup) to avoid prompt loops.

## License
MIT
