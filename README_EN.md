# Codex Usage Tray

Windows 11 system tray application that reads Codex usage from the local `codex app-server` over stdio JSONL.

Created by **Pongsathon.T** · [MIT License](LICENSE) · [ภาษาไทย](README.md)

This personal project is unofficial and is not affiliated with OpenAI.

## Features

- Runs as a `WinExe` without a console window.
- Tray icon styles: `Ring`, `Solid bar`, `Segmented bar`, and `Number only`.
- Usage popup styles: `Classic`, `macOS style`, and `Paper style`.
- Dark Windows 11-style right-click menu.
- Displays every rate-limit bucket returned by the server.
- Supports Weekly-only accounts, `rateLimitsByLimitId`, fallback `rateLimits`, `primary`, and `secondary`.
- Displays reset credits and token activity when the installed Codex version provides them.
- Displays local token totals and an estimated Standard API-equivalent cost by project.
- Provides `Usage details` for the last 7 days, last 30 days, all time, daily activity, and every local project.
- Uses an incremental local index so unchanged Codex session files are not parsed again.
- Refreshes adaptively every 1, 2, or 5 minutes, with manual, notification, and resume refresh.
- Checks public GitHub Releases manually, in notify-only mode, or with opt-in automatic installation.
- Verifies the GitHub asset SHA-256, ZIP paths, size limits, and internal `SHA256SUMS.txt`.
- Shows `Data stale` when cached usage is older than 10 minutes.
- Uses a single instance and closes its child `codex app-server` process on exit.
- Supports Start with Windows through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Requirements

- Windows 11 x64
- Codex Desktop or Codex CLI, signed in
- .NET 10 SDK only when building from source

The published package is self-contained. Target machines do not need a separate .NET runtime or Administrator permission.

## Install

Download the latest `CodexUsageTray-X.Y.Z-win-x64.zip` from [GitHub Releases](https://github.com/159shadows357/CodexUsageTray/releases), extract every file, and run:

```powershell
.\CodexUsageTray-Setup.exe
```

Setup installs the application under:

```text
%LOCALAPPDATA%\Programs\CodexUsageTray
```

It enables Start with Windows and launches the tray app by default.

Install without startup:

```powershell
.\CodexUsageTray-Setup.exe --no-startup
```

Uninstall:

```powershell
.\CodexUsageTray-Setup.exe --uninstall
```

Uninstall preserves settings, cache, and logs under `%LOCALAPPDATA%\CodexUsageTray`.

## Build and test

```powershell
pwsh -File .\scripts\build.ps1
pwsh -File .\scripts\test.ps1
dotnet format .\CodexUsageTray.slnx --verify-no-changes --no-restore
```

The test project uses a package-free runner. The current suite contains 61 tests.

Run from source:

```powershell
dotnet run --project .\src\CodexUsageTray\CodexUsageTray.csproj -c Release
```

Publish the application:

```powershell
pwsh -File .\scripts\publish.ps1 -Version 0.4.0
```

Create the installer ZIP:

```powershell
pwsh -File .\scripts\package-release.ps1 -Version 0.4.0
```

Output:

```text
artifacts\publish\win-x64\CodexUsageTray.exe
artifacts\release\CodexUsageTray-0.4.0-win-x64.zip
artifacts\release\CodexUsageTray-0.4.0-win-x64.zip.sha256
```

The ZIP contains `CodexUsageTray-Setup.exe`, `CodexUsageTray.exe`, `README.txt`, `LICENSE.txt`, and `SHA256SUMS.txt`.

## Authenticode

The EXE and Setup metadata identifies the publisher as `Pongsathon.T`. A public release is Authenticode-signed only when a valid code-signing certificate with an accessible private key is supplied:

```powershell
$thumbprint = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
  Where-Object HasPrivateKey |
  Select-Object -First 1 -ExpandProperty Thumbprint
pwsh -File .\scripts\package-release.ps1 `
  -Version 0.4.0 `
  -CodeSigningThumbprint $thumbprint
```

Do not use a self-signed certificate for a public release. Target machines will not trust it and Windows SmartScreen can still warn.

## Tray icon styles

Open `Tray icon style` from the right-click menu:

- `Ring`: default quota ring without a number.
- `Solid bar`: continuous fill gauge.
- `Segmented bar`: four-segment gauge.
- `Number only`: remaining percentage without the `%` character, label, frame, or card.

The exact percentage and reset time remain available in the tooltip.

## Popup styles

Open `Popup style` from the right-click menu:

- `Classic`: compact dark layout.
- `macOS style`: blue dark background, rounded cards, progress bars, account, and token activity.
- `Paper style`: light paper background, offset shadow, monospace values, and dark progress bars.

The popup displays only the buckets and optional data returned by the server. It does not invent a 5-hour bucket or reset-credit expiry dates.

## API-equivalent estimate

`API Equivalent` is an estimate of what local Codex session tokens might cost at Standard API rates. It is not a ChatGPT or Codex plan charge.

```text
uncached input = input - cached input - cache-write input

estimated USD =
  uncached input × input rate
  + cached input × cached-input rate
  + cache-write input × cache-write rate
  + output × output rate
```

`gpt-5.3-codex-spark` uses the published `gpt-5.3-codex` rate as a proxy while Spark has no separate public price. Unknown models stay `unpriced` and are not added to the estimate.

The application reads `%USERPROFILE%\.codex\sessions\**\*.jsonl` in read-only mode and uses only `cwd`, model, timestamp, and token counters. It does not read prompts, responses, or `auth.json`.

## Refresh behavior

- Usage changed or popup/details visible: 1 minute.
- Normal state: the configured base interval, default 2 minutes.
- Three unchanged refreshes or battery power: at least 5 minutes.
- App-server notification, manual refresh, and resume: immediate refresh.
- Connection failures: backoff of 2, 5, 10, 30, and 60 seconds.

## Public updates

Open `Updates` from the right-click menu:

- `Check now`: check the latest public release immediately.
- `Off`: disable background checks; this is the default.
- `Notify only`: check 30 seconds after startup and every 24 hours.
- `Install automatically`: download, verify, and start Setup without another prompt.

Update and download requests do not send an `Authorization` header. Target machines do not need a GitHub token.

Before installation, the updater verifies:

1. Exact asset name `CodexUsageTray-X.Y.Z-win-x64.zip`.
2. GitHub `asset.digest` SHA-256.
3. Download and extracted size limits.
4. Normalized ZIP paths to prevent path traversal.
5. `CodexUsageTray-Setup.exe` and `CodexUsageTray.exe` against `SHA256SUMS.txt`.

Automatic installation is opt-in while public executables are not Authenticode-signed.

## Codex CLI troubleshooting

Check the login:

```powershell
codex login status
```

If the PATH entry under `C:\Program Files\WindowsApps` returns `Access is denied`, locate the per-user executable created by Codex Desktop:

```powershell
$codex = Get-ChildItem "$env:LOCALAPPDATA\OpenAI\Codex\bin\*\codex.exe" -File |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 -ExpandProperty FullName
& $codex login status
```

The application checks `CODEX_CLI_PATH`, Codex configuration, the per-user Codex Desktop binary, usable PATH entries, and then VS Code extension binaries.

## Runtime data and privacy

Runtime files are stored under `%LOCALAPPDATA%\CodexUsageTray`:

```text
settings.json
usage-cache.json
local-usage-index.json
notification-state.json
updates\
logs\codex-usage-tray-yyyyMMdd.log
```

`local-usage-index.json` stores hashed session/project keys and aggregate counters. It does not store full paths, prompts, or responses.

Logs are retained for up to 7 days. Diagnostics exclude email, access tokens, refresh tokens, GitHub tokens, API keys, authorization headers, credentials, raw authentication responses, and environment dumps.

## Support the project

If this program helps your workflow, you can support its development and testing costs at [Ko-fi.com/PongsathonTan](https://ko-fi.com/PongsathonTan).

The Widget's `Buy me a coffee...` action opens Ko-fi directly.

## License

MIT License · Copyright © 2026 Pongsathon.T
