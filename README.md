# NoBSSftp

A no-nonsense, tabbed SFTP client built with Avalonia UI and .NET 10. It focuses on practical remote file workflows, secure credential handling on macOS, and external-terminal-first SSH usage.

## Features
- Multi-tab SFTP sessions
- Server explorer with groups, drag-and-drop ordering, and quick connect
- Native app menu support (`View` and `Help`) with toggles for server explorer and transfer queue visibility
- File explorer with CRUD, context menus, keyboard shortcuts, and drag-move
- Explorer view mode switcher (`List` / `Tree`)
- External file/folder drag-and-drop upload (recursive directories supported)
- File and directory download to local destination (recursive directories supported)
- Conflict prompts for transfers when destination items already exist
- Transfer queue with pending/running/completed/failed states, progress, cancel, and retry
- Search in remote explorer: instant current-folder filter plus optional recursive tree search
- Symlink-aware explorer and operations (explicit link indicators and follow-vs-link actions for open/copy/delete)
- Optional post-transfer integrity verification (SHA-256 for files up to 64 MB, size/time fallback for larger files)
- Atomic overwrite uploads (stage to temporary remote file, then promote only after successful upload/verification)
- Centered connecting/loading indicator while establishing SFTP sessions
- External SSH terminal launch from the active session
- SSH host key trust flow with fingerprint confirmation and persisted trust store
- Trusted-host-key management UI (`Help -> Manage Trusted Host Keys...`) for inspect/revoke/reset trust decisions
- Secure credential persistence via macOS Keychain
- Verified quick-connect on saved servers via macOS LocalAuthentication (Touch ID / device auth)
- Per-profile connection resilience controls (timeout, keepalive, reconnect strategy/retries)
- File/folder properties management (`chmod`/`chown`/`chgrp`) with optional recursive apply for directories

## Tech Stack
- .NET 10 (TargetFramework: net10.0)
- Avalonia UI 11.3.x (MVVM)
- SSH.NET for SFTP/SSH
- CommunityToolkit.Mvvm for MVVM helpers

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
dotnet publish NoBSStfp/src/NoBSSftp.csproj -c Release -r osx-arm64 -o NoBSStfp/output/publish
```

## Notes
- AOT + trimming is enabled; JSON serialization uses source generation for compatibility.
- Terminal access is provided via your OS terminal app (`ssh`) from inside the session UI.
- Saved profile credentials are persisted in macOS Keychain via native Security.framework calls and stripped from `servers.json`.
- Reusing saved secrets from the connect dialog requires macOS device-owner verification (Touch ID/password).
- After successful verification, an in-memory unlock session is reused for subsequent credential reads (short-lived) and can be manually locked from `View -> Lock Credential Session`.
- Keychain secrets are fetched on-demand during connect flows (not at app startup) to avoid prompt loops.
- Trusted host keys can be reviewed and removed via `Help -> Manage Trusted Host Keys...`.
- Integrity verification is opt-in from the connected toolbar (`Verify` checkbox).
- Reconnect behavior is profile-driven and applies to both initial connect attempts and transient runtime disconnects.
- Window title and About dialog version are resolved from assembly metadata (`NoBSSftp.csproj` version).

## License
MIT
