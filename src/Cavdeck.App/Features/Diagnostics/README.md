# Diagnostics

`DiagnosticLog` writes bounded local event records to `%LOCALAPPDATA%\CavdeckData\Logs`.
Each record contains UTC time, a random 12-character error ID, a fixed area, and a
fixed error category. No exception message, stack, URL, path, bucket, object key,
account, request payload, or credential is written. The report copies these records
plus app, OS, and runtime versions. It validates persisted records before copying.

Settings exposes **Copy diagnostic report** and **Open log folder** in both themes
and languages. Logs are kept for seven days, with a 1 MiB cap per daily file.
Writing errors are ignored so diagnostics cannot trigger a second failure.
Routine records are queued to a background worker so a slow disk does not block
the UI. Fatal handlers flush synchronously before the process exits. Preparing a
clipboard report also runs off the UI thread and shows a loading indicator.

Unhandled UI errors are recorded and cause shutdown after a generic prompt. A
process-level handler records other fatal errors. Unobserved task errors and
asynchronous command failures are recorded without showing private exception text.
Transfer failure transitions record their typed failure category. The transfer
journal remains the source for individual task state and retry history.

No diagnostic data is sent from the application. Users choose when to copy and
share a report.
