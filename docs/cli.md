# Cavdeck CLI

Cavdeck CLI runs on Windows x64. It shares the desktop app's connections in `%APPDATA%\Cavdeck\connections.json` and stores credentials in Windows Credential Manager under the same Windows user.

CLI help, status messages, and errors are displayed in English regardless of the Windows display language. This does not change the desktop app's language setting or prevent Unicode connection names, local paths, and S3 object keys.

## Install and run

Cavdeck CLI is included in the same Windows x64 installer as the desktop app. Install Cavdeck using the official [GitHub Releases](https://github.com/imnbwd/Cavdeck/releases) Setup.exe or MSI. Both installers use a per-user installation and add the installed CLI directory (`current\cli`) to the current user's PATH.

Open a new terminal after installation and run:

```powershell
cavdeck --help
cavdeck --version
cavdeck connection list --json
```

The CLI is installed under the same Velopack-managed application directory but keeps its own dependencies in a `cli` subdirectory. There is no separate CLI installer or CLI ZIP to download, and no separate .NET 10 installation is required. Desktop and CLI are updated and uninstalled together. The CLI shares the desktop app's connections in `%APPDATA%\Cavdeck\connections.json` and credentials in Windows Credential Manager under the same Windows user.

The desktop updater will not apply an update while a CLI process is running. Close active CLI commands and retry the update. New CLI commands are rejected while an update is being applied. When manually updating or uninstalling with Setup.exe or MSI, close all CLI processes first; Velopack's installer hooks cannot cancel a manual install or uninstall.

Build from source with the .NET 10 SDK:

```powershell
dotnet publish src/Cavdeck.Cli/Cavdeck.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/cli/win-x64
```
## Commands

| Command | Arguments and command-specific options |
| --- | --- |
| `connection list` | None |
| `connection show` | `<name-or-id>` |
| `connection test` | `<name-or-id>` |
| `connection add` | `--name <name> --provider <provider>`, provider settings and credentials |
| `connection update` | `<name-or-id>` and settings to change |
| `connection remove` | `<name-or-id> (--dry-run or --yes)` |
| `bucket list` | `-c <name-or-id>` |
| `object list`, `ls` | `<s3-uri> -c <name-or-id> [--recursive] [--limit <1–1000>] [--page-token <token>]` |
| `object stat`, `stat` | `<s3-uri> -c <name-or-id>` |
| `object search`, `search` | `<query> --in <s3-uri> -c <name-or-id> [--match name|key-prefix] [--page-token <token>]` |
| `object mkdir`, `mkdir` | `<s3-prefix/> -c <name-or-id>` |
| `object copy`, `cp` | `<source-uri> <destination-uri>`, connection options, `[--recursive] [--on-conflict <policy>] [--yes]` |
| `object move`, `mv` | Same as copy, with required `--yes` |
| `object remove`, `rm` | `<s3-uri> -c <name-or-id> (--dry-run or --yes) [--recursive] [--max-objects <1–100000>]` |
| `object presign`, `presign` | `<s3-uri> -c <name-or-id> [--expires-in <60–604800>]` |
| `transfer upload`, `upload` | `<local-path> <s3-uri> -c <name-or-id> [--recursive] [--on-conflict <policy>]` |
| `transfer download`, `download` | `<s3-uri> -c <name-or-id> (--to-file <file> or --to-dir <dir>) [--recursive] [--on-conflict <policy>]` |
| `transfer list` | `[--limit <1–500>] [--kind <kind>] [--state <state>]` |
| `transfer show` | `<run-id> [--job <job-id>] [--limit <1–500>] [--offset <n>] [--kind <kind>] [--state <state>]` |
| `transfer resume` | `<run-id> [--job <job-id>]` |

`--connection` is the long form of `-c`. Names match case-insensitively. Use the GUID if names are ambiguous. Storage commands require an explicit connection.

```powershell
cavdeck connection show prod-r2
cavdeck connection test prod-r2
cavdeck bucket list -c prod-r2
cavdeck ls 's3://assets/images/' -c prod-r2 --limit 50
cavdeck stat 's3://assets/images/季度 报告.pdf' -c prod-r2
```

The URI scheme applies to all S3-compatible providers. Keys preserve literal whitespace, percent escapes, and repeated slashes. Leading slashes in keys, dot segments, control characters, and keys beyond Cavdeck's 1,020-byte UTF-8 limit are rejected. A scoped connection uses its configured prefix for bucket-root listings and recursive transfers. Explicit keys and prefixes must be within the connection's scope.

Listing returns one page, including in recursive mode. The default limit is 200. Pass the returned `nextPageToken` to `--page-token` with the same connection, bucket, prefix, and recursion settings. Results distinguish common prefixes, file objects, and zero-byte objects ending in `/` (`directoryMarker`). S3 can fold child markers into common prefixes in a delimiter listing. Use `--recursive` to inspect the marker objects themselves.

## Output and help

By default, commands display concise resource-specific tables. For example, `connection list` shows names, providers, regions and read-only state; `object list` shows object keys, types, sizes and modification times. Long fields are shortened in tables only. Add `--wide` to see full values and additional columns, or `--json` to retrieve the complete machine-readable result. `--wide` never changes JSON output.

```powershell
cavdeck connection list
cavdeck connection list --wide
cavdeck ls 's3://assets/' -c prod-r2 --wide
cavdeck connection add --help
```

Missing or invalid options report an English error with a stable error code and exit code. Syntax diagnostics do not echo raw argument values because they may contain credentials or other sensitive data. Run `cavdeck --help` or `cavdeck <group> --help` for available commands and options.

## Connection management

Pass a secret through an environment variable named by `--secret-env`. You can also name an access key ID variable with `--access-key-id-env`. Use `--access-key-id` for the ID if appropriate for your environment. In an interactive terminal you can enter the secret at a hidden prompt. Use environment variables with `--no-input`, JSON output, or redirected input.

```powershell
cavdeck connection add --name aws-prod --provider aws-s3 --region us-east-1 --access-key-id-env CAVDECK_AWS_KEY_ID --secret-env CAVDECK_AWS_SECRET --no-input
cavdeck connection add --name prod-r2 --provider cloudflare-r2 --account-id YOUR_ACCOUNT_ID --access-key-id-env CAVDECK_R2_KEY_ID --secret-env CAVDECK_R2_SECRET --no-input
cavdeck connection add --name local-minio --provider minio --endpoint https://minio.example.com --force-path-style --access-key-id-env MINIO_KEY_ID --secret-env MINIO_SECRET --no-input
cavdeck connection update prod-r2 --name production-r2 --read-only
cavdeck connection remove production-r2 --dry-run
```

AWS requires a region. R2 generates its endpoint from the account ID and uses region `auto`. MinIO and `s3-compatible` require an endpoint and default to `us-east-1`. Use `--restricted-bucket` and `--key-prefix` to scope a connection.

Update preserves settings and credentials unless you supply changes. Credential rotation requires both a new access key ID and a secret. Use `--writable`, `--no-force-path-style`, `--clear-endpoint`, `--clear-restricted-bucket`, and `--clear-key-prefix` to unset the corresponding settings, subject to provider requirements. Names are unique case-insensitively for new connections and renames. Existing ambiguous names can be addressed by GUID. Removing a saved connection requires `--yes`.

## Object management

```powershell
cavdeck search logo --in 's3://assets/images/' -c prod-r2 --json
cavdeck search '2026/' --in 's3://assets/' -c aws-prod --match key-prefix
cavdeck mkdir 's3://assets/archive/' -c aws-prod
cavdeck cp 's3://assets/logo.png' 's3://assets/archive/logo-v2.png' -c aws-prod
cavdeck cp 's3://assets/images/' 's3://backup/images/' --source-connection aws-prod --destination-connection local-minio --recursive
cavdeck mv 's3://assets/report.pdf' 's3://assets/archive/' -c aws-prod --yes
cavdeck rm 's3://assets/old/' -c aws-prod --recursive --dry-run
cavdeck rm 's3://assets/old/' -c aws-prod --recursive --yes --max-objects 1000
cavdeck presign 's3://assets/report.pdf' -c aws-prod --expires-in 3600 --json
```

Name search is a case-insensitive substring match. Key-prefix search matches a case-sensitive full object key prefix within the requested scope. Each segment uses up to 20 list requests, scans up to 20,000 objects, returns up to 2,000 matches, and has a 20-second budget. When `complete` is false, pass `nextPageToken` with the same connection, bucket, scope, query, and match mode. Large checkpoints use a local token that references a file under `%LOCALAPPDATA%\CavdeckData\cli\search`. Keep the file while the token is in use. It can contain object keys and search terms.

`mkdir` atomically creates a zero-byte object at a slash-terminated key. An existing marker produces a conflict. A single-file copy or move uses the explicit destination key, or appends the basename when the destination ends in `/`. Recursive copies and moves require `--recursive` and a slash-terminated destination, preserve relative keys, and build a manifest of at most 20,000 objects before copying. Specify either `-c` for both endpoints or both `--source-connection` and `--destination-connection`.

Copy and move use the transfer conflict policies below. Copy defaults to `fail`. Overwrite and move require `--yes`. Move verifies the destination content before conditionally deleting the source. Each operation returns a run ID. Providers without verified conditional multipart completion use a conditional single PUT when available, subject to the server and the CLI's 1 GiB transfer staging budget.

`rm --dry-run` reports the object count and sample keys. Execution requires `--yes` and builds a fresh ETag manifest before deleting anything. The default limit is 10,000 objects. Bucket roots are prohibited. A partial failure reports completed deletions and errors separately. CLI conditional deletion currently requires a native AWS S3 source. Other sources can be copied, and deletion previews can still be read when the connection permits them.

Presigned download links default to 3,600 seconds. They are bearer tokens that grant anyone with the URL access until expiry. Treat the output as sensitive.

## Transfers

A trailing slash on an upload destination appends the source filename. An explicit object key is used verbatim. A recursive upload places the selected directory's contents under a slash-terminated destination and uses zero-byte markers for empty directories.

```powershell
cavdeck upload '.\logo.png' 's3://assets/images/' -c prod-r2
cavdeck transfer upload '.\logo.png' 's3://assets/images/logo-v2.png' -c prod-r2 --on-conflict overwrite
cavdeck upload '.\photos' 's3://assets/photos/' -c prod-r2 --recursive
```

A single object downloads to the exact path given by `--to-file` or to its basename under `--to-dir`. Recursive download requires `--to-dir` and a slash-terminated remote source. It preserves paths relative to the prefix and skips zero-byte directory markers. Windows names that would require rewriting and case-insensitive collisions are reported as failures.

```powershell
cavdeck download 's3://assets/images/logo.png' -c prod-r2 --to-file '.\local logo.png'
cavdeck download 's3://assets/images/logo.png' -c prod-r2 --to-dir '.\downloads'
cavdeck transfer download 's3://assets/images/' -c prod-r2 --recursive --to-dir '.\images'
```

| Policy | Behavior |
| --- | --- |
| `fail` | Default. Refuse an existing destination. |
| `skip` | Keep an existing destination and count the transfer as skipped. |
| `overwrite` | Permit replacement. Remote replacement uses the destination ETag as a write condition. |
| `rename` | Choose an available numbered sibling if the initial destination exists. |

A competing write can still cause a conflict after a destination is selected. New objects use the server-side `If-None-Match: *` condition. New local files use a same-directory move without replacement. AWS S3 supports conditional multipart completion. Cloudflare R2 and MinIO use conditional single-request PUT up to 5 GiB, subject to the server's request limit. MinIO endpoints must enforce S3 conditional PUT. Unknown S3-compatible upload providers return `UNSUPPORTED_CONDITIONAL_WRITE`.

Read-only connections cannot upload. Transfer paths containing reparse points are rejected. Recursive transfers report successful, skipped, failed, and cancelled counts, with details for up to 100 failures. Earlier successes remain in place after a later failure.

## Automation

Global options include `--help` / `-h`, `--version`, `--format table|json`, `--json`, `--quiet` / `-q`, `--verbose`, `--no-color`, and `--no-input`. Output is plain text by default. JSON mode, redirected streams, and `--quiet` suppress progress. Errors remain on stderr.

```powershell
$json = cavdeck ls 's3://assets/images/' -c prod-r2 --recursive --limit 25 --json
if ($LASTEXITCODE -ne 0) {
    throw "Cavdeck failed with exit code $LASTEXITCODE"
}
$page = $json | ConvertFrom-Json
$page.data.items
```

Success writes one JSON document to stdout. Failure writes one JSON document to stderr. Transfer errors can include a run ID and counts in `error.details`.

```json
{"schemaVersion":1,"operation":"object.list","data":{"items":[],"nextPageToken":null},"warnings":[]}
```

```json
{"schemaVersion":1,"error":{"code":"ACCESS_DENIED","message":"Access denied for this storage operation.","retryable":false,"details":null},"diagnostic":null}
```

| Exit | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected operation, worker, or journal failure |
| 2 | Invalid arguments |
| 3 | Connection, bucket, or object missing |
| 4 | Authentication or authorization failure |
| 5 | Destination conflict |
| 6 | Network or storage service unavailable |
| 7 | Partial transfer failure |
| 8 | Local file error |
| 9 | Unsupported operation |
| 130 | Cancelled with Ctrl+C |

## Transfer history and recovery

Each command waits for its own jobs to finish. Transfer state lives in `%LOCALAPPDATA%\CavdeckData\cli\runs\<run-id>\transfers.db`, separate from the desktop queue. The folder uses the run ID without hyphens and also contains `run.json` and `run.lock`. Logs can contain local paths and object keys. Keep them in the user's profile.

```powershell
cavdeck transfer list --state failed --kind upload --json
cavdeck transfer show $runId --limit 50 --offset 0 --json
cavdeck transfer show $runId --job $jobId --json
cavdeck transfer resume $runId --no-input --json
```

List and show open the journal in SQLite read-only mode. Show returns `truncated` and `nextOffset` when there are more jobs. Both commands default to 100 results. A run marked `interrupted` has unfinished state with no active lock. Older P0 journals can be inspected and appear as legacy runs.

Resume acquires the original run's exclusive lock and reuses its job and batch IDs. It considers queued, paused, interrupted, waiting-retry, and retryable failed jobs. A job must still match its connection settings and stored source and destination checkpoints. Completed, cancelled, non-retryable failed, and cleanup-pending jobs are excluded. Older P0 runs lack the required connection checkpoints for safe resume.

Ctrl+C stops scanning and cancels active work. The CLI waits for its workers, then attempts to remove partial files and abort known incomplete multipart sessions for cancelled jobs. Retryable failures retain their checkpoints. Forced process exit can leave temporary files or multipart sessions behind. A provider can also create a session before returning the upload ID to the client.

Logs are retained until manually removed. Delete a run directory only after the process has exited and its result confirms terminal jobs with no pending cleanup. Retain logs for interrupted runs or unknown cleanup state until temporary files and incomplete multipart sessions have been inspected.


## Real provider tests

Set `CAVDECK_CLI_TEST_<PROVIDER>_ENABLED=1` with `BUCKET`, `PREFIX`, `ACCESS_KEY`, and `SECRET_KEY` under the same variable prefix. Provider names are `AWS`, `R2`, and `MINIO`. Set `ENDPOINT` for R2 or MinIO and `REGION` as needed. Use a dedicated test bucket and least-privilege credentials for a test prefix beginning with `cavdeck-test/`. Each test uses a unique key and attempts to clean up its test objects. Tests without the required configuration are skipped.
