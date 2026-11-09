# Cavdeck CLI

Cavdeck CLI runs on Windows x64 and uses the S3-compatible connections saved by the desktop app under the same Windows user. It reads `%APPDATA%\Cavdeck\connections.json` and retrieves keys from Windows Credential Manager only when a command needs them. If the catalog requires recovery, open the desktop app to recover it.

## Install and run

Extract the entire Windows x64 CLI artifact. Keep its DLLs and runtime files beside `cavdeck.exe`. Run it from that directory or add the directory to PATH. The self-contained artifact includes .NET 10.

```powershell
.\cavdeck.exe --help
.\cavdeck.exe --version
.\cavdeck.exe connection list --json
```

Build from source with the .NET 10 SDK.

```powershell
dotnet publish src/Cavdeck.Cli/Cavdeck.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/cli/win-x64
```

## Commands

| Command | Arguments and command-specific options |
| --- | --- |
| `connection list` | None |
| `connection show` | `<name-or-id>` |
| `connection test` | `<name-or-id>` |
| `bucket list` | `-c <name-or-id>` |
| `object list`, `ls` | `<s3-uri> -c <name-or-id> [--recursive] [--limit <1–1000>] [--page-token <token>]` |
| `object stat`, `stat` | `<s3-uri> -c <name-or-id>` |
| `transfer upload`, `upload` | `<local-path> <s3-uri> -c <name-or-id> [--recursive] [--on-conflict <policy>]` |
| `transfer download`, `download` | `<s3-uri> -c <name-or-id> (--to-file <file> or --to-dir <dir>) [--recursive] [--on-conflict <policy>]` |

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

Global options include `--help` / `-h`, `--version`, `--format table|json`, `--json`, `--quiet` / `-q`, `--verbose`, `--no-color`, and `--no-input`. Output is plain text by default. Commands never prompt. JSON mode, redirected streams, and `--quiet` suppress progress. Errors remain on stderr.

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

## Run logs and cleanup

Each command waits for its own jobs to finish. Transfer state lives in `%LOCALAPPDATA%\CavdeckData\cli\runs\<run-id>\transfers.db`, separate from the desktop queue. The folder uses the run ID without hyphens. Logs can contain local paths and object keys. Keep them in the user's profile.

Ctrl+C stops scanning and cancels active work. The CLI waits for its workers, then attempts to remove partial files and abort known incomplete multipart sessions. Forced process exit can leave temporary files or multipart sessions behind. A provider can also create a session before returning the upload ID to the client.

Logs are retained until manually removed. Delete a run directory only after the process has exited and its result confirms terminal jobs with no pending cleanup. Retain logs for interrupted runs or unknown cleanup state until temporary files and incomplete multipart sessions have been inspected.

Connection editing, bucket management, object mutation commands, sync, background transfers, cross-process job control, and resume commands are outside P0.

## Real provider tests

Set `CAVDECK_CLI_TEST_<PROVIDER>_ENABLED=1` with `BUCKET`, `PREFIX`, `ACCESS_KEY`, and `SECRET_KEY` under the same variable prefix. Provider names are `AWS`, `R2`, and `MINIO`. Set `ENDPOINT` for R2 or MinIO and `REGION` as needed. Use a dedicated test bucket and least-privilege credentials for a test prefix beginning with `cavdeck-cli-test/`. Each test uses a unique key and attempts to clean up its test objects. Tests without the required configuration are skipped.
