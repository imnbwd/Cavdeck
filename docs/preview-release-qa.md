# Cavdeck 1.4 — Preview Release QA

This is the release gate for PRs 1–5 (Preview Foundation, Rich Image Preview,
Thumbnail Pipeline and Media Preview). CI results **do not** substitute for running
the live AWS S3, Cloudflare R2, MinIO and interactive Windows scenarios below.

## 1. Automated release gate

On Windows with .NET 10 SDK:

    dotnet restore Cavdeck.sln
    dotnet build Cavdeck.sln --no-restore -c Release
    dotnet test Cavdeck.sln --no-build -c Release
    ./scripts/New-WindowsPackage.ps1 -Version 0.0.1 -SkipTests
    ./scripts/New-WindowsCliPackage.ps1 -Version 0.0.1
    ./scripts/Test-WindowsInstall.ps1 -ReleasesDirectory artifacts/windows-package/0.0.1/Releases -Version 0.0.1 -DisposableMachine

The existing Build & Test workflow runs these steps plus packaged CLI smoke tests for
each PR. All pre-existing Desktop, Application, Infrastructure, Domain and CLI
tests must still pass. The cloud integration tests are **skipped** if real
credentials are absent; that does not count as cloud acceptance.

## 2. AWS S3 / Cloudflare R2 / MinIO live integration gate

Use a dedicated disposable test bucket for each provider. Restrict credentials
to that bucket and a prefix starting with **cavdeck-cli-test/**. Do not use
production account credentials or a production bucket.

The v1.3 CLI cloud suite and the v1.4 preview suite use the same environment
variables. Set separately for each prefix **CAVDECK_CLI_TEST_AWS_**,
**CAVDECK_CLI_TEST_R2_** and **CAVDECK_CLI_TEST_MINIO_**:

| Suffix | Required | Details |
| --- | --- | --- |
| ENABLED | Yes | Must equal 1 to opt in |
| BUCKET | Yes | Dedicated writable test bucket |
| PREFIX | Yes | e.g. cavdeck-cli-test/qa/ |
| ACCESS_KEY | Yes | Limited test credential |
| SECRET_KEY | Yes | Limited test credential |
| ENDPOINT | R2 and MinIO | S3-compatible endpoint reachable from runner |
| REGION | No | AWS defaults to us-east-1; R2 defaults to auto |

Do not put secrets in tracked files or PR comments. The manual GitHub Actions
workflow **Preview Cloud Acceptance** reads repository Actions secrets with
these exact variable names and allows choosing AWS, R2, MINIO or all. For
MinIO, the server must be reachable from the Windows runner.

On Windows with environment variables configured:

    dotnet test tests/Cavdeck.Cli.Tests/Cavdeck.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~PreviewCloudTests|FullyQualifiedName~CloudStorageTests" --logger "console;verbosity=normal"

**Do not mark cloud acceptance complete if provider tests are Skipped.** For
each backend, require successful execution of both the v1.3 CLI cloud test
and the v1.4 preview cloud test.

The new preview tests create unique keys inside the isolated prefix; verify
remote HEAD/list, text and image reads, metadata-only PDF/media selection,
explicit bounded PDF/audio/video downloads, byte integrity, temporary file
disposal, and cancellation. All fixture keys are deleted after each run.
The original CLI suite also exercises copy, move where supported, download,
presigned GET and conditional writes.

These automated tests verify data transport and contracts, **not** visual
WPF playback, native codec support or WebView2 rendering.

## 3. Interactive Windows desktop gate

Use an installed x64 build with small real PNG/JPEG, PDF, WAV/MP3, H.264 MP4
and intentionally unsupported codec. Repeat representative operations on AWS,
R2 and MinIO. Record dated evidence (provider, Windows version, screenshots)
in the release issue before checking each item off.

- [ ] Text: UTF-8 Chinese/English/Japanese/German preview; edit, save conflict,
  read-only permission, unsaved-change prompt, refresh and retry.
- [ ] PDF: select then rapidly select another file, cancel, reopen, switch tabs,
  verify sandboxed browser navigation and local copy cleanup.
- [ ] Image: fit, 1:1, mouse-anchored zoom, drag, previous/next in sorted order,
  no stale image on rapid cancellation or navigation.
- [ ] Thumbnails: virtualized list/tiles, visible-only fetching, cancelled reads
  on scroll and tab switch, 4 MiB fallback and 16 MiB bounded cache.
- [ ] Audio: selection causes no media download; explicit playback, pause, seek,
  skip, mute/volume, tab switch/close and cancellation.
- [ ] Video: MP4 playback and unsupported codec fallback, F11, resize and aspect
  ratio; Esc/Space/arrow/M controls preserve slider keyboard accessibility.
- [ ] Loading/errors: offline, forbidden, deleted remote object, oversized
  item and cancellation show helpful states with retry or fallback.
- [ ] Localization: all four languages; switch language while loading/playing
  and while image error is visible. Check German/Japanese clipping.
- [ ] Theme/DPI: light/dark at 100%, 150%, 200% scale; move image/video
  windows between monitors without loss of zoom or unreadable controls.
- [ ] Cleanup: close during download and playback; change workspaces; repeat
  30 open/close cycles. No lingering audio, windows, runaway memory or temp
  files. Codec-locked files retain the normal two-day prune fallback.
- [ ] v1.3 baseline: connections, credential restoration, object listing,
  search, upload, download, copy/move/delete, transfer history, auto-update,
  tray actions, shortcuts and the installed CLI still function.

## 4. Release decision

Only mark **1.4 ready** when: (a) full Windows CI build/test/installer gate
passes, (b) AWS/R2/MinIO live tests are Passed (not Skipped), (c) desktop
visual/media QA and the v1.3 baseline are signed off, and (d) no P0/P1
regressions remain. Unsupported Windows codecs are a documented compatibility
limitation, not universal format support.
