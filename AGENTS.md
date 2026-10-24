# Repository instructions

## Scope

Codex Usage Tray is a public, personal Windows 11 utility created by Pongsathon.T. It is a C# WinForms application targeting `net10.0-windows` and `win-x64`.

Use the existing project structure and built-in .NET APIs. Do not add a NuGet package, framework, service, or paid dependency unless the user explicitly approves it.

## Public repository policy

- The only committed Markdown files are `AGENTS.md`, `README.md`, and `README_TH.md`.
- Keep the Thai and English README content aligned when behavior, commands, requirements, version, security, installation, update, support, or license details change.
- Do not recreate `prompt.md`, project-state files, handoff files, design-QA Markdown, or separate Markdown documentation.
- Put release validation evidence in the GitHub release notes or the user handoff, not a new Markdown file.
- Do not commit `artifacts/`, `.artifacts/`, logs, caches, generated schemas, screenshots, credentials, tokens, user paths, or machine-specific settings.

## Runtime boundaries

- Read account quota from the local `codex app-server`.
- Initialize the protocol with `capabilities.experimentalApi = true`.
- Support `rateLimitsByLimitId` and fallback `rateLimits`, including `primary` and `secondary`.
- Display only buckets returned by the server. Never invent a 5-hour bucket.
- Calculate `remainingPercent = clamp(100 - usedPercent, 0, 100)`.
- Default `Tray quota source` to `Frontier only` as explicitly selected by the user. Keep `Lowest remaining bucket` available and persist the choice. Show every bucket in the popup (use pages when needed, never scrollbars).
- In `Frontier only`, require exact `limitId = codex` (case-insensitive); never substitute Spark when unavailable. The icon, tooltip and first popup quota card must identify the same selected source.
- Treat optional or unknown account/usage fields as unavailable and keep the application running.
- Support ChatGPT Plus and other plans from server-returned windows; never infer entitlement, fixed allowances, or reset-credit availability from the plan name.
- Keep Tibo community forecasts separate from account quota and credit actions. Use the free public codex-reset.com API with project User-Agent, attribution, at least one minute between requests, and stale/unavailable states.
- Read local session JSONL in read-only mode only for `cwd`, model, timestamp, and token counters.
- Never read Codex authentication files or store prompts/responses.

## UI boundaries

- Normal runtime has no console and no taskbar button.
- Left click opens the usage popup; right click opens `ModernContextMenuStrip`.
- Keep the dark menu palette, rounded border, item padding, hover state, separators, and matching submenu renderer.
- Keep all popup styles and tray icon styles selectable and persisted.
- Do not add scrollbars to the usage popup. `Usage details` may scroll only inside its project table.
- Use actual usage changes for activity visuals. Do not add random animation.
- Dispose icons, bitmaps, graphics objects, regions, native handles, forms, HTTP clients, and child processes.

## Public updater

- Repository: `159shadows357/CodexUsageTray`.
- Updates use the public GitHub Releases API without an `Authorization` header.
- Keep update modes `Off`, `NotifyOnly`, and `Automatic`; default remains `Off`.
- Select the exact canonical asset `CodexUsageTray-year.month.day.patch-win-x64.zip`. Publish an identical three-part-named compatibility ZIP for legacy 0.6.x updaters that discard the fourth part.
- Require GitHub `asset.digest` SHA-256, size limits, HTTPS GitHub redirect allowlist, safe normalized ZIP paths, and internal `SHA256SUMS.txt`.
- Automatic installation remains opt-in while the binaries are not Authenticode-signed.
- `AppLinks.Support` targets `https://ko-fi.com/PongsathonTan`.

## Version updates

Keep the same release version in:

- `src/CodexUsageTray/CodexUsageTray.csproj`
- `src/CodexUsageTray.Setup/CodexUsageTray.Setup.csproj`
- default version in `scripts/publish.ps1`
- default version in `scripts/package-release.ps1`
- `clientInfo.version` in `src/CodexUsageTray/CodexAppServerSession.cs`
- commands and output examples in both README files

Use calendar versions and tags such as `2026.10.24.1` / `v2026.10.24.1` (year.month.day.daily-patch). Preserve all four version parts in EXE metadata, app-server clientInfo, updater comparison, staging paths and canonical asset names.

## Validation

Run from the repository root:

```powershell
pwsh -NoLogo -NoProfile -File .\scripts\build.ps1
pwsh -NoLogo -NoProfile -File .\scripts\test.ps1
dotnet format .\CodexUsageTray.slnx --verify-no-changes --no-restore
pwsh -NoLogo -NoProfile -File .\scripts\package-release.ps1 -Version year.month.day.patch
```

Required evidence:

- Release build: 0 warnings and 0 errors.
- Every package-free test passes.
- Format verification passes.
- ZIP contains Setup, app, `README.txt`, `LICENSE.txt`, and `SHA256SUMS.txt`.
- Outer ZIP SHA-256 matches the `.sha256` file.
- Both EXE hashes match `SHA256SUMS.txt`.
- EXE and Setup product version and publisher metadata are correct.
- Authenticode status is recorded honestly.
- Tray/menu/popup are checked on Windows 11 at real desktop scale.
- Live app-server data matches the Codex usage panel and no server-absent bucket appears.
- Installed app starts once, preserves Start with Windows, and leaves no orphan child process.
- Public release API and asset download work without a GitHub token.

## Release process

1. Confirm the worktree contains only intended changes.
2. Run the complete validation above.
3. Commit and push the reviewed source.
4. Build the final package from the commit that will receive the version tag.
5. Create and push the `vyear.month.day.patch` tag.
6. Publish a GitHub release with the ZIP and `.sha256` assets.
7. Verify the public API response, digest, anonymous download, installation, and updater.
8. Keep release notes concise and include changes, SHA-256, test count, Authenticode status, and known limitations.

When a valid current-user code-signing certificate is available, pass its thumbprint to `scripts/package-release.ps1`. Do not publish a self-signed certificate as a SmartScreen workaround.
