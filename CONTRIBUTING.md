# Contributing to Cavdeck

Thanks for helping improve Cavdeck. This guide covers development setup, project conventions, testing, and pull requests.

Cavdeck is a **fully free, Windows-native S3-compatible storage manager**, licensed under [GPL-3.0-or-later](LICENSE). We favor reliable storage operations, clear behavior, small maintainable changes, and a consistent Windows experience.

## Before starting

1. Search existing [Issues](https://github.com/imnbwd/Cavdeck/issues) and [Pull Requests](https://github.com/imnbwd/Cavdeck/pulls) to avoid duplicate work.
2. Use the [Bug report](.github/ISSUE_TEMPLATE/bug_report.yml) or [Feature request](.github/ISSUE_TEMPLATE/feature_request.yml) template. For larger changes or new dependencies, discuss the motivation and trade-offs in an Issue before implementing.
3. For security vulnerabilities, **do not create a public Issue**. Follow [SECURITY.md](SECURITY.md).
4. Keep pull requests focused. Unrelated refactoring, UI restyling, and feature additions should be separate.

## Development setup

**Requirements:** Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and optionally Visual Studio with the .NET desktop development workload. See the [README and usage guide](README.md#usage-notes).

From PowerShell at the repository root, use Debug for local development:

```powershell
dotnet restore Cavdeck.sln
dotnet build Cavdeck.sln -c Debug --no-restore
dotnet test Cavdeck.sln -c Debug --no-build
dotnet run --project src/Cavdeck.App/Cavdeck.App.csproj -c Debug
```

Before submitting a code change, also validate the Release configuration when possible:

```powershell
dotnet build Cavdeck.sln -c Release --no-restore
dotnet test Cavdeck.sln -c Release --no-build
```

Manually verify affected behavior. Builds and unit tests do **not** replace checks against real storage providers or an installed release. The [CI workflow](.github/workflows/ci.yml) restores, builds, and tests for every PR and main push. Full installer packaging and installation checks run only for manually dispatched CI runs, reducing hosted Runner usage.

### Documentation screenshots

On Windows, run `dotnet run -c Debug --project src/Cavdeck.App -- --demo-snapshot`.
This renders the real WPF shell with four **fictional** storage connections (AWS S3, Cloudflare R2, MinIO, and custom S3), then writes `explorer-light.png`, `explorer-dark.png`, `connections-light.png`, and `settings-light.png` to `%TEMP%\\cavdeck-demo-screenshots`.
No storage server, saved credentials, or personal connection settings are loaded. Capture at 100% Windows display scaling for consistent image dimensions; review the resulting PNGs before publishing.

## Architecture at a glance

The solution has four layers:

| Project | Responsibility |
| --- | --- |
| `Cavdeck.Domain` | Storage and transfer domain rules |
| `Cavdeck.Application` | Use cases, contracts, browsing and transfer coordination |
| `Cavdeck.Infrastructure` | S3 integrations, persistence, Windows credentials and updates |
| `Cavdeck.App` | WPF UI, MVVM, themes, localization and dependency registration |

The `Cavdeck.App` project builds the `Cavdeck` assembly and publishes `Cavdeck.exe`; keep the Velopack `--mainExe` argument aligned with that filename. The project name and `Cavdeck.App` namespace remain unchanged.

`App.xaml.cs` composes the application. Keep AWS SDK types in Infrastructure, not ViewModels. Credentials are stored through `WindowsCredentialVault`, not the connection catalog; `TransferEngine` and `SqliteTransferJournal` coordinate transfers and local recovery.

For user-visible behavior, limitations, and cross-connection copy safeguards, see [Using Cavdeck](README.md#usage-notes). Detailed WPF and localization rules are in [AGENTS.md](AGENTS.md).

## Project conventions

- Preserve the architecture boundaries described above; do not over-engineer new abstractions.
- Follow [AGENTS.md](AGENTS.md) for WPF binding, accessibility, visual styles, icons, and localization conventions.
- **All user-visible strings** must use localized resources, not hardcoded text. Keep English, Simplified Chinese, Japanese, and German resource keys and formatting placeholders consistent. Check longer translated strings and runtime language switching.
- For UI changes, check light/dark/system appearance, keyboard and focus behavior, and relevant DPI scales. Preserve established interaction patterns.
- For transfers, deletion, overwrites, signed URLs, and cross-connection operations, preserve safety checks, cancellation, error handling, and transfer-state correctness. Do not assume S3, R2, and MinIO have identical capabilities.
- Add focused regression tests for behavior changes and update documentation when user-facing behavior or limitations change. Avoid adding third-party dependencies without an explicit need and license review.

## Secrets, privacy, and test data

- **Never commit** access keys, secret keys, tokens, presigned URLs, private buckets, personal file paths, production credentials, or copied customer data.
- Use synthetic fixtures and temporary, least-privilege resources when real-provider tests are necessary; avoid destructive testing against production buckets.
- Secrets belong in Windows Credential Manager, not settings JSON, logs, screenshots, issues, or CI artifacts. Respect existing diagnostic redaction rules.
- Review diffs, test output, and attached screenshots for sensitive values before submitting.

## Pull requests

1. Create a descriptive branch (for example, `fix/transfer-cancel` or `docs/getting-started`).
2. Make a minimal change, test it, and keep the diff reviewable.
3. Open a PR against `main` using the [pull request template](.github/PULL_REQUEST_TEMPLATE.md). Describe the problem, the approach, and testing performed. Mark untested cases explicitly rather than claiming validation.
4. Link related Issues when applicable. If UI changes, add **redacted** before/after screenshots when useful.
5. Respond to review feedback and keep the branch up to date. Merges are at maintainer discretion.

## Release validation

Release preparation and publication are maintainer responsibilities. The [CI workflow](.github/workflows/ci.yml) builds and tests each PR and `main` push; manually triggered CI runs also package and smoke-test installers. Passing CI does not prove compatibility with real storage providers or safe behavior after installation.

### Package rehearsal

On Windows, use the .NET SDK specified in `global.json` and Velopack CLI 1.2.161:

```powershell
dotnet tool install --global vpk --version 1.2.161
./scripts/New-WindowsPackage.ps1 -Version 0.0.1
```

The script builds and tests Cavdeck, creates the versioned `Cavdeck-<version>-Setup.exe` and `Cavdeck-<version>.msi` installers, verifies the update package and feed, and writes `SHA256SUMS.txt` under `artifacts/windows-package/<version>/Releases/`. The Setup installer targets per-user installation; MSI supports per-user or per-machine installation. Both GitHub installers are currently **unsigned**.

Test installation, uninstallation, and first launch on a disposable Windows VM. The automated installer smoke test does not replace checking the interactive installer, credentials, themes, languages, or real storage operations. Do not run [`Test-WindowsInstall.ps1`](scripts/Test-WindowsInstall.ps1) against a development workstation with an existing Cavdeck installation.

### Draft releases

The manual [release workflow](.github/workflows/release.yml) runs from `main` using the `production-release` GitHub Environment. It prepares a **draft** GitHub Release for review; it never publishes automatically.

Maintainers can instead run the same process locally on a trusted Windows machine with .NET 10, Git, GitHub CLI, and Velopack CLI 1.2.161:

```powershell
gh auth login
git switch main
git pull --ff-only
./scripts/Publish-WindowsRelease.ps1 -Version 0.1.0
```

The script requires a clean `main` checkout matching `origin/main`, rejects invalid release versions, and creates an unsigned draft with installers, update files, checksums, and release notes. It does **not** install or uninstall the application by default. Use `-DisposableMachine` only on a clean disposable VM. Local publication bypasses GitHub Environment approvals, so maintainers must enforce the same review requirements manually.

### Publication checks

1. Inspect Git history, release assets, and screenshots for secrets or private data. Review transitive licenses and redistributed notices using [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
2. Protect `main`, configure release approvals where supported, and verify the private vulnerability-reporting path described in [SECURITY.md](SECURITY.md) before publishing the repository.
3. Validate AWS S3, Cloudflare R2, and MinIO behavior with disposable buckets and least-privilege credentials. Keep sensitive test evidence outside source control.
4. Test both installers and a **two-version upgrade** in a clean VM. Confirm that connections, settings, favorites, and transfer history survive, and that active transfers or unsaved work cannot be interrupted unsafely.
5. Verify downloaded installer SHA-256 checksums, the stable update feed, and all required third-party notices. Clearly disclose that installers are unsigned; checksums do not establish publisher identity.
6. Publish the reviewed draft manually only after these checks pass. Publishing a stable release makes it available to installed clients through the official GitHub update feed.

## Licensing

The repository uses [GPL-3.0-or-later](LICENSE). By submitting a contribution, you agree that your original contribution may be distributed under that license. Submit only material you have the right to license, and identify third-party material and any required attribution. Do not copy proprietary or incompatible-license code, images, fonts, or other assets into the repository.

There is currently no separate contributor license agreement or mandatory signed-off-by procedure.
