# Cavdeck CLI User Guide

Cavdeck CLI lets you manage S3-compatible storage from PowerShell or another Windows terminal. You can list buckets, upload and download files, search objects, copy or move data between connections, create download links, and inspect or resume transfer runs.

**Platform:** Windows x64. **Version covered:** Cavdeck CLI 1.3.0. **Command:** `cavdeck`.

The CLI and Cavdeck Desktop use the same saved connections **when run as the same Windows user**, but maintain separate transfer queues and history. The CLI does not require the desktop window to be open. Its help, messages, and errors are in English; Unicode connection names, paths, and object keys are supported.

> **Start here:** [Install](#1-installation) → [Quick start](#2-quick-start) → [Common tasks](#6-common-tasks). For every command, see [Command reference](#5-command-reference).

## Contents

1. [Installation](#1-installation)
2. [Quick start](#2-quick-start)
3. [Connections and credentials](#3-connections-and-credentials)
4. [S3 paths and connection scope](#4-s3-paths-and-connection-scope)
5. [Command reference](#5-command-reference)
6. [Common tasks](#6-common-tasks)
7. [Transfers, conflicts, and recovery](#7-transfers-conflicts-and-recovery)
8. [Scripts and AI agents](#8-scripts-and-ai-agents)
9. [Security and provider limits](#9-security-and-provider-limits)
10. [Troubleshooting](#10-troubleshooting)

## 1. Installation

### Option A: Desktop + CLI (recommended)

Download **Cavdeck-1.3.0-Setup.exe** or **Cavdeck-1.3.0.msi** from the [official GitHub Releases](https://github.com/imnbwd/Cavdeck/releases/tag/v1.3.0). Both install the desktop app and CLI for the current Windows user and add the installed CLI directory to the **user PATH**.

Open a **new** PowerShell or terminal window after installation:

```powershell
cavdeck --version
cavdeck --help
cavdeck connection list
```

The normal Setup.exe location is `%LOCALAPPDATA%\Cavdeck\current\cli\cavdeck.exe`. MSI installations may use a different installation directory. The CLI has its own dependencies under `current\cli`, while sharing the desktop installation, update, and uninstall lifecycle.

### Option B: CLI-only ZIP

Download **Cavdeck-Cli-1.3.0-win-x64.zip** from the same release, extract **all** files into one directory, and run:

```powershell
# From the extracted directory:
.\cavdeck.exe --version
.\cavdeck.exe --help
```

To call it from any directory, add the extracted folder to your user PATH. The ZIP is self-contained; **you do not need to install .NET 10 separately**. It does not receive desktop automatic updates or uninstall with the desktop app. Replace its files yourself when upgrading.

The ZIP can be used without Cavdeck Desktop. In that case, use `cavdeck connection add` to create connections. If Desktop is already installed for the same Windows user, the ZIP-based CLI can use its saved connections.

### Verify the download

Release installers are currently **not code-signed**; Windows may display an Unknown Publisher or SmartScreen warning. Download only from the official release and compare the SHA-256 result with that release's checksum table or `SHA256SUMS.txt`:

```powershell
Get-FileHash '.\Cavdeck-1.3.0-Setup.exe' -Algorithm SHA256
Get-FileHash '.\Cavdeck-Cli-1.3.0-win-x64.zip' -Algorithm SHA256
```

### Updates and running commands

The installed desktop app and CLI update together. The desktop updater will not start an update while a CLI command is running; commands started during an update are rejected. Close CLI processes before a manual Setup.exe/MSI update or uninstall.

## 2. Quick start

These examples use **PowerShell**. Replace connection names, bucket names, and local paths with your own values. `my-bucket` is a placeholder: **Cavdeck CLI does not create a bucket in these steps**.

### A. If you already have a desktop connection

```powershell
cavdeck connection list
cavdeck connection test prod-r2
cavdeck bucket list -c prod-r2
cavdeck ls 's3://my-bucket/' -c prod-r2
```

`prod-r2` is an example connection name. To discover yours, run `connection list` first. Every storage command requires an explicit connection (`-c` or `--connection`).

### B. If you have no saved connection

For an AWS S3 connection, supply your access key ID; the CLI then asks for the **secret access key in a hidden prompt** when run interactively:

```powershell
cavdeck connection add --name aws-demo --provider aws-s3 --region us-east-1 --access-key-id YOUR_ACCESS_KEY_ID
cavdeck connection test aws-demo
cavdeck bucket list -c aws-demo
```

Enter the real key ID in place of `YOUR_ACCESS_KEY_ID`. For R2 or MinIO, see [Connections and credentials](#3-connections-and-credentials). For scripts or JSON mode, use environment variables instead of the interactive secret prompt.

### C. Upload and download one file

Use a connection that can **write** to an existing bucket. The following commands are a complete round trip:

```powershell
'Hello from Cavdeck' | Set-Content -Path '.\cavdeck-demo.txt' -Encoding UTF8

cavdeck upload '.\cavdeck-demo.txt' 's3://my-bucket/cli-demo/' -c aws-demo
cavdeck ls 's3://my-bucket/cli-demo/' -c aws-demo
cavdeck download 's3://my-bucket/cli-demo/cavdeck-demo.txt' -c aws-demo --to-file '.\cavdeck-demo-copy.txt'
```

An upload to a destination ending in `/` appends the source filename. To specify the complete remote object key, omit the trailing slash and include the filename. An existing destination is **not overwritten by default**.

## 3. Connections and credentials

Connections are shared with Desktop for the same Windows user. Non-secret settings live in `%APPDATA%\Cavdeck\connections.json`; credentials use **Windows Credential Manager**. Do not edit either store by hand. Connection names match case-insensitively; if older data contains ambiguous names, use the GUID from `connection list --json`.

### Supported providers

| Provider | `--provider` value | Required configuration | Notes |
| --- | --- | --- | --- |
| Amazon S3 | `aws-s3` | `--region` | Example region: `us-east-1` |
| Cloudflare R2 | `cloudflare-r2` | `--account-id` | Endpoint derived from account ID; region is `auto` |
| MinIO | `minio` | `--endpoint` | Region defaults to `us-east-1`; `--force-path-style` is available |
| Other S3-compatible storage | `s3-compatible` | `--endpoint` | Region defaults to `us-east-1`; provider behavior may vary |

### Add a connection

For interactive use, provide an access key ID and enter the secret at the hidden prompt:

```powershell
cavdeck connection add --name aws-prod --provider aws-s3 --region us-east-1 --access-key-id YOUR_ACCESS_KEY_ID
cavdeck connection add --name local-minio --provider minio --endpoint https://minio.example.com --force-path-style --access-key-id YOUR_ACCESS_KEY_ID
```

For automation, first provision the required **process environment variables** through a trusted local or CI secret mechanism, then reference their names (not their values):

```powershell
cavdeck connection add --name prod-r2 --provider cloudflare-r2 --account-id YOUR_ACCOUNT_ID --access-key-id-env CAVDECK_R2_KEY_ID --secret-env CAVDECK_R2_SECRET --no-input
```

`CAVDECK_R2_KEY_ID` and `CAVDECK_R2_SECRET` must already exist in the process environment. `--secret-env` accepts an **environment variable name**, not the secret itself. The CLI does not provide a `--secret` argument. Do not put secret values in command arguments, scripts checked into source control, or chat/agent logs.

### Inspect, test, update, and remove

```powershell
cavdeck connection list --wide
cavdeck connection show prod-r2
cavdeck connection test prod-r2
cavdeck connection update prod-r2 --name production-r2 --read-only
cavdeck connection update production-r2 --writable
cavdeck connection remove production-r2 --dry-run
# Execute only after checking the preview:
cavdeck connection remove production-r2 --yes
```

Removing a connection removes its saved configuration; **it does not delete remote objects**. Removal accepts **exactly one** of `--dry-run` or `--yes`.

You may change a connection's provider settings, read-only state, restricted bucket, or key prefix with `connection update`. Unspecified fields and credentials are preserved. To rotate credentials, supply **both** a new access key ID (`--access-key-id` or `--access-key-id-env`) and a new secret (hidden prompt or `--secret-env`).

| Connection option | Meaning |
| --- | --- |
| `--name` | Connection display name (required on add) |
| `--provider` | One of the four provider identifiers above (required on add) |
| `--region`, `--endpoint`, `--account-id` | Provider-specific settings |
| `--access-key-id`, `--access-key-id-env` | Literal access key ID **or** name of an environment variable containing it; not both |
| `--secret-env` | Name of an environment variable containing the secret; otherwise interactive hidden prompt when credentials are required |
| `--force-path-style` | Enable path-style requests |
| `--read-only` | Disallow write operations |
| `--restricted-bucket`, `--key-prefix` | Limit the connection's accessible bucket or object-key prefix |
| `--writable`, `--no-force-path-style` | Update-only options to reverse the corresponding setting |
| `--clear-endpoint`, `--clear-restricted-bucket`, `--clear-key-prefix` | Update-only clearing options, subject to provider requirements |

Conflicting options (for example, `--read-only` and `--writable`) are rejected. Some provider-specific settings cannot be overridden: R2 derives its endpoint from `--account-id` and uses region `auto`.

## 4. S3 paths and connection scope

All S3-compatible providers use the same remote URI shape:

```text
s3://bucket-name
s3://bucket-name/path/to/file.txt
s3://bucket-name/photos/
```

The `s3://` scheme does **not** mean the provider must be AWS. `-c` chooses the actual connection and endpoint.

- `s3://bucket-name/` refers to the bucket root or a prefix for listing.
- `s3://bucket-name/file.txt` refers to a specific object key.
- A trailing `/` on a remote destination means "place the file under this prefix" for single-file copy/upload. Recursive copy/download operations require appropriately slash-terminated prefixes.
- A "folder" in S3 is usually an **object-key prefix**, not a filesystem directory. `mkdir` creates a zero-byte directory marker.

Quote remote URIs and local paths in PowerShell, especially when they contain spaces or Unicode:

```powershell
cavdeck stat 's3://my-bucket/reports/季度 报告.pdf' -c prod-r2
```

Object keys preserve literal percent signs/escapes, spaces, and repeated slashes: **do not URL-decode or normalize keys**. Keys with a leading `/`, `.` or `..` path segments, control characters, or more than **1,020 UTF-8 bytes** are rejected.

Connections scoped with `--restricted-bucket` or `--key-prefix` enforce those restrictions. Root-oriented operations use the configured scope where supported; explicit object keys/prefixes must fall within it. A scope is not a substitute for appropriately restricted provider credentials.

## 5. Command reference

Use `cavdeck --help`, `cavdeck object --help`, or `cavdeck <group> <command> --help` to inspect the installed version. Running `cavdeck` or a group name with no subcommand shows help.

### Global options

These are available throughout the command tree.

| Option | Behavior |
| --- | --- |
| `-h`, `--help` | Show help |
| `--version` | Show CLI version |
| `-c`, `--connection <name-or-id>` | Required by storage commands; select a saved connection |
| `--json` | Return one JSON result; same mode as `--format json` |
| `--format table\|json` | Choose table or JSON output; `--json --format table` is invalid |
| `--wide` | Show full values and more columns in table output; no effect on JSON |
| `-q`, `--quiet` | Suppress progress, **not** normal results or errors |
| `--verbose` | Include limited, sanitized diagnostics for errors |
| `--no-color` | Request non-colored output |
| `--no-input` | Do not prompt for input (useful in scripts) |

`-c` is **command-specific**, not a global root option. It is listed here because storage commands use it consistently. Other options listed above are global.

### Connections and buckets

| Command | Arguments and options | Purpose |
| --- | --- | --- |
| `connection list` | None | List saved connections |
| `connection show` | `<name-or-id>` | Inspect one connection (no secret value) |
| `connection test` | `<name-or-id>` | Test stored credentials/connection |
| `connection add` | `--name`, `--provider`, credentials, provider settings | Save a new connection |
| `connection update` | `<name-or-id>` and fields to change | Update settings or rotate credentials |
| `connection remove` | `<name-or-id> (--dry-run \| --yes)` | Preview or remove saved connection |
| `bucket list` | `-c <name-or-id>` | List accessible buckets |

### Objects

Use `cavdeck object <command>` for the grouped form, or its top-level alias where shown.

| Grouped command / alias | Main arguments and options | Purpose |
| --- | --- | --- |
| `object list` / `ls` | `<s3-uri> -c <connection> [--recursive] [--limit 1..1000] [--page-token <token>]` | List one page of objects/prefixes |
| `object stat` / `stat` | `<s3-uri> -c <connection>` | Get object metadata |
| `object search` / `search` | `<query> --in <s3-uri> -c <connection> [--match name\|key-prefix] [--page-token <token>]` | Search keys under a prefix |
| `object mkdir` / `mkdir` | `<s3-prefix/> -c <connection>` | Create a directory marker |
| `object copy` / `cp` | `<source-uri> <dest-uri>` + connection selection, `[--recursive] [--on-conflict <policy>] [--yes]` | Copy objects |
| `object move` / `mv` | Same as `cp`, but **requires `--yes`** | Copy, verify, then conditionally delete source |
| `object remove` / `rm` | `<s3-uri> -c <connection> (--dry-run \| --yes) [--recursive] [--max-objects 1..100000]` | Preview or delete objects |
| `object presign` / `presign` | `<s3-uri> -c <connection> [--expires-in 60..604800]` | Create a temporary download URL |

**Copy/move connection selection:** either pass `-c <connection>` to use the **same connection for both ends**, or pass **both** `--source-connection <name-or-id>` and `--destination-connection <name-or-id>`. Do not combine these forms.

**Listing:** Default `--limit` is `200`; `--recursive` still returns **one page**, not the entire bucket. The response can contain `file`, `prefix`, or `directoryMarker` entries. Pass `nextPageToken` to `--page-token` with unchanged connection, bucket, prefix, and recursion settings. A zero-byte key ending in `/` is a directory marker; recursive listing can reveal markers hidden behind common prefixes in non-recursive listings.

**Search:** Default `--match name` is a case-insensitive filename substring match. `--match key-prefix` matches a **case-sensitive full key prefix** within the requested scope. Search returns bounded segments (up to 20 list requests, 20,000 scanned objects, 2,000 matches, or a 20-second budget per segment). If `complete` is `false`, continue with `nextPageToken` and identical search inputs. Large search tokens can be local checkpoints under `%LOCALAPPDATA%\CavdeckData\cli\search`; keep those files until the search is complete.

**Delete:** `rm` requires exactly one of `--dry-run` or `--yes`; a prefix ending in `/` also needs `--recursive`. Bucket-root deletion is prohibited. Default `--max-objects` is `10000`, with a maximum of `100000`. Dry run returns object counts and up to 20 sample keys. **In the current implementation even `rm --dry-run` requires a writable connection**, although it does not execute a deletion. Only a native AWS S3 source supports the CLI's verified conditional deletion required for `rm --yes`; other sources may support preview but not execution.

**Presign:** Default lifetime is `3600` seconds. Support depends on the provider. The returned URL grants whoever holds it access until expiration; do not expose it in logs.

### Transfers and history

| Command | Arguments and options | Purpose |
| --- | --- | --- |
| `transfer upload` / `upload` | `<local-path> <s3-uri> -c <connection> [--recursive] [--on-conflict <policy>]` | Upload file or directory |
| `transfer download` / `download` | `<s3-uri> -c <connection> (--to-file <path> \| --to-dir <path>) [--recursive] [--on-conflict <policy>]` | Download file or prefix |
| `transfer list` | `[--limit 1..500] [--kind <kind>] [--state <state>]` | List local transfer runs |
| `transfer show` | `<run-id> [--job <job-id>] [--limit 1..500] [--offset <n>] [--kind <kind>] [--state <state>]` | Inspect a run and its jobs |
| `transfer resume` | `<run-id> [--job <job-id>]` | Attempt safe recovery of eligible jobs |

`transfer list` and `transfer show` default to 100 results; use `--offset` with `transfer show` to page jobs. `transfer show` returns `truncated` and `nextOffset` when more jobs exist.

**Filter values:** `--kind` accepts `upload`, `download`, `copy`, `move`. `--state` accepts job states `queued`, `running`, `paused`, `waitingretry`, `completed`, `failed`, `cancelled` (case-insensitive; `waitingretry` has **no hyphen**).

A run summary can show status `interrupted` when work has stopped without completing, but `interrupted` is **not** an accepted `--state` filter value. The state filter applies to **jobs**, whereas the displayed run status is a summary.

## 6. Common tasks

These examples use existing connections and buckets. Check the destination and permissions before running commands that change data.

### Browse, inspect, and search

```powershell
cavdeck bucket list -c prod-r2
cavdeck ls 's3://assets/images/' -c prod-r2 --limit 50
cavdeck ls 's3://assets/images/' -c prod-r2 --recursive --wide
cavdeck stat 's3://assets/images/logo.png' -c prod-r2
cavdeck search logo --in 's3://assets/images/' -c prod-r2
cavdeck search 'images/2026/' --in 's3://assets/images/' -c prod-r2 --match key-prefix
```

To fetch a second listing page in PowerShell:

```powershell
$pageText = cavdeck ls 's3://assets/images/' -c prod-r2 --limit 50 --json
if ($LASTEXITCODE -ne 0) { throw 'Listing failed' }
$page = $pageText | ConvertFrom-Json

if ($page.data.nextPageToken) {
    cavdeck ls 's3://assets/images/' -c prod-r2 --limit 50 --page-token $page.data.nextPageToken --json
}
```

This fetches at most two pages. A complete listing requires repeating until `nextPageToken` is null.

### Upload a file or folder

```powershell
# Keep the original filename under images/:
cavdeck upload '.\logo.png' 's3://assets/images/' -c prod-r2

# Choose an exact remote key:
cavdeck upload '.\logo.png' 's3://assets/images/logo-v2.png' -c prod-r2

# Upload a directory's contents under photos/:
cavdeck upload '.\photos' 's3://assets/photos/' -c prod-r2 --recursive
```

A recursive upload requires an existing local directory and a slash-terminated remote destination. Empty local directories become zero-byte folder markers. Re-running a transfer does **not** automatically synchronize deletions or overwrite existing files.

### Download an object or folder

```powershell
# Exact local filename:
cavdeck download 's3://assets/images/logo.png' -c prod-r2 --to-file '.\logo-copy.png'

# Keep the remote basename under a local directory:
cavdeck download 's3://assets/images/logo.png' -c prod-r2 --to-dir '.\downloads'

# Download a remote prefix recursively:
cavdeck download 's3://assets/images/' -c prod-r2 --recursive --to-dir '.\images'
```

`--to-file` and `--to-dir` are mutually exclusive; supply exactly one. Recursive download requires `--to-dir` and a remote source ending in `/`. The CLI skips zero-byte directory markers. Invalid Windows filenames, case-insensitive local name collisions, or paths containing reparse points can cause failures rather than silent renaming.

### Create a folder, copy, or move

```powershell
cavdeck mkdir 's3://assets/archive/' -c aws-prod

# Same connection:
cavdeck cp 's3://assets/logo.png' 's3://assets/archive/logo.png' -c aws-prod

# Across connections and buckets:
cavdeck cp 's3://assets/images/' 's3://backup/images/' --source-connection aws-prod --destination-connection local-minio --recursive

# Move one object; the source must support verified conditional deletion:
cavdeck mv 's3://assets/report.pdf' 's3://assets/archive/' -c aws-prod --yes
```

`mkdir` creates a marker rather than a real directory and fails if that marker exists. Recursive copy/move requires `--recursive` and a slash-terminated destination, preserves relative object keys, and builds a manifest of **at most 20,000 objects** before copying. Source/destination overlaps are rejected. Each copy/move returns a transfer run ID. Move verifies destination content before conditionally deleting the source; it is **not** an atomic rename.

### Preview and delete safely

```powershell
cavdeck rm 's3://assets/old/' -c aws-prod --recursive --dry-run
# After reviewing scope and counts, run only if you intend to delete:
cavdeck rm 's3://assets/old/' -c aws-prod --recursive --yes --max-objects 1000
```

The CLI builds a fresh ETag manifest for execution, so a previous preview is **not** a frozen or guaranteed deletion set. Individual completed deletions are not rolled back if later objects fail. Use limited credentials and inspect the dry run first.

### Create a temporary download link

```powershell
cavdeck presign 's3://assets/report.pdf' -c aws-prod --expires-in 3600
```

Treat the URL as a **temporary bearer credential**. A presigned link does not make the bucket public.

## 7. Transfers, conflicts, and recovery

### Conflict policies

Upload, download, copy, and move accept `--on-conflict`. The default is `fail`.

| Policy | What happens if the destination exists |
| --- | --- |
| `fail` | Stop/refuse the conflicting destination (default) |
| `skip` | Leave it unchanged and count the item as skipped |
| `overwrite` | Permit replacement, using remote write preconditions where supported |
| `rename` | Use an available numbered sibling name |

A competing write can still produce a conflict after the destination is checked. Completed items remain completed if another item fails. The CLI does **not** silently retry an unsafe overwrite.

**Confirmation is intentionally different between commands:**

| Action | Need `--yes`? |
| --- | --- |
| Upload or download, including `--on-conflict overwrite` | No |
| Remote `cp --on-conflict overwrite` | **Yes** |
| Remote `mv`, with any conflict policy | **Yes** |
| `rm` execution | **Yes** (`--dry-run` instead for preview) |
| `connection remove` execution | **Yes** (`--dry-run` instead for preview) |

### Execution and transfer history

A CLI upload, download, copy, or move **waits for its own jobs to finish**. It does not enqueue work into the Desktop Transfers panel.

Each run stores a local SQLite journal at:

```text
%LOCALAPPDATA%\CavdeckData\cli\runs\<run-id-without-hyphens>\transfers.db
```

The same directory contains `run.json` and `run.lock`. Journal entries can include local paths and remote keys, so keep them private.

```powershell
cavdeck transfer list --json
cavdeck transfer list --kind upload --state failed --json
cavdeck transfer show YOUR_RUN_ID --limit 50 --offset 0 --json
cavdeck transfer show YOUR_RUN_ID --job YOUR_JOB_ID --json
```

Replace `YOUR_RUN_ID` and `YOUR_JOB_ID` with IDs from command output or `transfer list` / `transfer show`. Both GUIDs with and without hyphens are accepted.

A successful transfer command's JSON `data.runId` identifies its run:

```powershell
$responseText = cavdeck upload '.\report.pdf' 's3://assets/reports/' -c aws-prod --json
if ($LASTEXITCODE -ne 0) {
    throw 'Upload failed; inspect stderr and transfer list'
}
$response = $responseText | ConvertFrom-Json
$runId = $response.data.runId

cavdeck transfer show $runId --json
```

If the transfer **fails**, JSON error details may contain a `runId`; inspect stderr or use `cavdeck transfer list` to locate it.

### Resume interrupted or retryable work

```powershell
cavdeck transfer list --json
cavdeck transfer show YOUR_RUN_ID --json
cavdeck transfer resume YOUR_RUN_ID --no-input --json
```

You can also pass `--job YOUR_JOB_ID` to attempt one job. Resume reuses the original run and job identifiers, obtains an exclusive run lock, and checks saved connection/source/destination checkpoints.

Eligible work may include queued, paused, waiting-retry, or **retryable failed** jobs; a run reported as `interrupted` may contain such jobs. Completed, cancelled, non-retryable failed, and cleanup-pending jobs are not resumed. Legacy runs without the required checkpoints cannot be safely resumed.

**Cancellation:** `Ctrl+C` requests cancellation and the CLI waits for workers to stop and perform best-effort cleanup. A forced process kill can leave temporary files or incomplete multipart uploads; do not assume every remote side effect was rolled back.

**Retention:** Run journals remain until manually removed. Delete a run directory only when its process has exited, all jobs are terminal, and no cleanup is pending. Keep logs for interrupted runs or uncertain multipart cleanup.

## 8. Scripts and AI agents

Cavdeck CLI can be called from PowerShell scripts, scheduled tasks, or AI agents using ordinary process execution. There is **no special agent protocol** required. Prefer JSON output and **noninteractive** execution.

```powershell
$jsonText = cavdeck ls 's3://assets/images/' -c prod-r2 --recursive --limit 25 --json --no-input
if ($LASTEXITCODE -ne 0) {
    throw "Cavdeck failed with exit code $LASTEXITCODE"
}

$result = $jsonText | ConvertFrom-Json
$result.data.items
$result.data.nextPageToken
```

**Output contract:**

- Success: one JSON document on **stdout** with `schemaVersion`, `operation`, `data`, and `warnings`.
- Failure: one JSON document on **stderr** with `schemaVersion`, `error`, and `diagnostic`.
- Progress is suppressed in JSON mode, quiet mode, or redirected streams. Human-readable errors still go to stderr in table mode.
- Check the **process exit code**, not only whether stdout contains text. Use `--no-input` for unattended jobs.

Example success:

```json
{"schemaVersion":1,"operation":"object.list","data":{"items":[],"nextPageToken":null},"warnings":[]}
```

Example error:

```json
{"schemaVersion":1,"error":{"code":"ACCESS_DENIED","message":"Access denied for this storage operation.","retryable":false,"details":null},"diagnostic":null}
```

Transfer errors may include the run ID and completion/failure counts in `error.details`; callers should inspect those details before retrying.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Unexpected operation, worker, integrity, or journal failure |
| `2` | Invalid arguments or configuration input |
| `3` | Connection, object, bucket, run, or job not found |
| `4` | Authentication or authorization failure |
| `5` | Conflict, busy catalog/run, or update in progress |
| `6` | Network/storage service unavailable or retryable timeout |
| `7` | Partial transfer failure |
| `8` | Local file or journal/configuration file error |
| `9` | Unsupported operation/configuration |
| `130` | Cancelled with Ctrl+C |

For automation, use `error.code` for more specific handling and `error.retryable` as guidance, **not** a guarantee that repeating a write is safe.

### Suggested AI agent safeguards

Provide the agent with a restricted connection and a small allowlist of operations. Use `ls`, `stat`, `search`, or `transfer show` for discovery. Require explicit user approval before `rm`, `mv`, `connection remove`, or overwrite operations. Do not give the agent raw access keys, presigned links, local transfer journals, or unrestricted shell permissions unless necessary. Validate remote URIs and avoid logging sensitive command output.

For scheduled tasks, the task must run under the **Windows user who owns the saved connections and credentials**. Different Windows accounts do not automatically share Windows Credential Manager entries.

## 9. Security and provider limits

Cavdeck uses provider credentials stored in Windows Credential Manager; remote operations are performed using the selected connection. Read-only and bucket/prefix restrictions are enforced by Cavdeck, but provider-side IAM/policies should also grant **least privilege**.

| Operation | Current implementation notes |
| --- | --- |
| New remote object writes | Use `If-None-Match: *` to guard against unexpected destination creation |
| Remote overwrite | Uses destination ETag/write conditions where supported |
| AWS S3 multipart upload | Supports conditional multipart completion |
| Cloudflare R2 and MinIO upload | Use conditional single-request PUT up to 5 GiB, subject to server request limits; MinIO must enforce conditional PUT |
| Other S3-compatible writes | Unknown providers can return `UNSUPPORTED_CONDITIONAL_WRITE` |
| Remote move | Checks source capability before scanning/copying; verified conditional source deletion is currently available for native AWS S3 sources |
| Remote delete | `rm --yes` requires verified conditional deletion, currently native AWS S3; `rm --dry-run` still requires a writable connection |
| Large cross-storage copy/move | A provider without verified conditional multipart completion may use conditional single PUT subject to the CLI's 1 GiB staging budget and server limits |
| Recursive copy/move | At most 20,000 source objects per operation |
| Recursive delete | Default 10,000 objects, configurable up to 100,000 |
| Transfer errors | Return up to 100 individual failure details; check truncation flags |
| Local transfers | Reject reparse points and unsafe Windows output paths |

Object-store writes and local writes are not atomic across an entire multi-object operation. Review partial-failure details and run history before retrying, especially for copy/move/delete.

## 10. Troubleshooting

| Symptom or error | What to check |
| --- | --- |
| `cavdeck` is not recognized | Open a new terminal. For a normal Setup.exe install, try `& "$env:LOCALAPPDATA\Cavdeck\current\cli\cavdeck.exe" --help`. For MSI/custom paths, locate the installed `cli` folder; for ZIP, use `.\cavdeck.exe` or add its folder to PATH. |
| No connections listed | Verify you are running as the same Windows user as Desktop. Or create a connection with `connection add`. |
| `CREDENTIALS_REQUIRED` | A secret is required. In an interactive console, omit `--secret-env` and enter it at the hidden prompt; for JSON, redirected, or `--no-input` use valid `--secret-env` and optionally `--access-key-id-env`. |
| `AUTH_FAILED` / `ACCESS_DENIED` | Check credentials, endpoint/region, IAM/bucket policy, and any connection read-only/bucket/prefix restriction. |
| `INVALID_ARGUMENTS` / `UNSUPPORTED_KEY` | Check `s3://bucket/key`, quoting, required `-c`, slash rules, key restrictions, and the command's `--help`. |
| `TARGET_CONFLICT` / `SOURCE_DESTINATION_OVERLAP` | Inspect source/destination. Use `skip` or `rename` if appropriate, or explicitly authorize an overwrite where supported. |
| `UNSUPPORTED_CONDITIONAL_WRITE` | The destination does not provide the verified conditional write required by this operation; use a supported provider/operation. |
| `UNSUPPORTED_CONDITIONAL_DELETE` | CLI move/delete execution requires a verified conditional source delete; native AWS S3 is currently supported. |
| `APPLICATION_UPDATING` | Finish the desktop update and retry after it completes. |
| `CONNECTION_CATALOG_BUSY` / `RUN_IN_PROGRESS` | Another process is editing the connection catalog or owns the transfer run. Allow it to finish; do not modify journal files while in use. |
| `INVALID_SEARCH_TOKEN` | Keep the original search inputs unchanged. If the token is `local.<id>`, make sure its local checkpoint file still exists for the same Windows user. |
| `RUN_NOT_FOUND` / `JOB_NOT_RESUMABLE` | Confirm run ID, local journal existence, eligible job status, and the availability of saved resume checkpoints. |
| `--state interrupted` fails | `interrupted` is a run-summary status, not a job-state filter. Use `transfer list --json` and filter the `status` field in your script. |
| Large download/upload is incomplete | Inspect `transfer list` / `transfer show`, then attempt `transfer resume` only if the job is eligible; verify source/destination and cleanup status first. |

When reporting a bug, include the Cavdeck version, exact command **with secrets and presigned URLs removed**, provider type, `error.code`, exit code, and the relevant transfer run ID. Do not publish `%APPDATA%` connection files, Windows credentials, or `%LOCALAPPDATA%` run journals.

---

## Build from source (for contributors)

Cavdeck CLI targets .NET 10 on Windows. From the repository root with the **.NET 10 SDK**:

```powershell
dotnet publish src/Cavdeck.Cli/Cavdeck.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/cli/win-x64
```

The published `cavdeck.exe` and all its runtime/dependency files must remain together. For real-provider integration-test configuration and test safety notes, see [tests/README.md](../tests/README.md). User installations do **not** require the .NET SDK.
