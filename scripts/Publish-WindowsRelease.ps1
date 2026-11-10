param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [switch]$DisposableMachine
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($env:OS -ne 'Windows_NT') { throw 'Cavdeck releases require Windows.' }

foreach ($command in @('dotnet', 'git', 'gh', 'vpk')) {
    if (!(Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Missing $command. Install the .NET 10 SDK, Git, GitHub CLI and vpk 1.2.161."
    }
}

try { $requested = [version]::Parse($Version) }
catch { throw "Invalid release version $Version." }
if ($requested.Major -gt 255 -or $requested.Minor -gt 255 -or $requested.Build -gt 65535) {
    throw 'Windows Installer version limit is 255.255.65535.'
}

if (![string]::IsNullOrWhiteSpace($env:VPK_SIGN_PARAMS)) {
    throw 'This workflow creates unsigned releases. Unset VPK_SIGN_PARAMS to avoid an unexpected signing configuration.'
}

$previousGhToken = $env:GH_TOKEN
$previousVpkToken = $env:VPK_TOKEN
Push-Location $root
try {
    # GitHub Actions provides GH_TOKEN. A local maintainer uses gh auth login.
    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $token = (& gh auth token | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
            throw 'Authenticate with gh auth login (Release write access required).'
        }
        $env:GH_TOKEN = $token
    }
    if ([string]::IsNullOrWhiteSpace($env:VPK_TOKEN)) {
        $env:VPK_TOKEN = $env:GH_TOKEN
    }

    if ($env:GITHUB_ACTIONS -eq 'true') {
        $repository = $env:GITHUB_REPOSITORY
        if ($env:GITHUB_REF -ne 'refs/heads/main') {
            throw 'GitHub Releases may only be prepared from main.'
        }
    }
    else {
        $repository = (& gh repo view --json nameWithOwner --jq .nameWithOwner | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the current GitHub repository.' }
        $branchName = (& git branch --show-current | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $branchName -ne 'main') {
            throw 'Run this command from the main branch.'
        }
        $dirty = @(& git status --porcelain)
        if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
            throw 'The working tree must be clean.'
        }
        $remoteLine = (& git ls-remote origin refs/heads/main | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteLine)) {
            throw 'Cannot verify origin/main.'
        }
        $remoteSha = ($remoteLine -split '\s+')[0]
        $currentSha = (& git rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $currentSha -ne $remoteSha) {
            throw 'Local HEAD must exactly match origin/main.'
        }
    }

    if ($repository -ne 'imnbwd/Cavdeck') {
        throw "Refusing to publish Cavdeck outside the official public repository (current repository: $repository)."
    }

    # A GitHub Release uses the v-prefixed tag as its source reference.
    # Refuse to reuse an existing tag, even when no corresponding Release exists.
    $tagName = "v$Version"
    $existingTag = @(& git ls-remote --tags origin "refs/tags/$tagName")
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to check whether remote tag $tagName already exists."
    }
    if ($existingTag.Count -gt 0) {
        throw "Git tag $tagName already exists. Refusing to reuse or overwrite an existing release tag."
    }

    $sourceSha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$') {
        throw 'Cannot identify the source commit SHA.'
    }

    $releases = @(gh release list --repo $repository --limit 1000 --json tagName,isPrerelease,isDraft | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read existing GitHub releases.' }
    foreach ($release in $releases) {
        if ($release.tagName -match '^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
            $existing = [version]::Parse($Matches[0].TrimStart('v'))
            if ($existing -eq $requested) {
                throw "Version $Version already exists, including draft releases."
            }
            if (!$release.isPrerelease -and !$release.isDraft -and $existing -gt $requested) {
                throw "Version $Version must exceed the published stable version $existing."
            }
        }
    }

    $output = Join-Path $root "artifacts/windows-package/$Version"
    $releaseDir = Join-Path $output 'Releases'
    if (Test-Path $output) {
        throw "The output directory already exists: $output. Inspect it before removing it."
    }

    Write-Host "Preparing unsigned Cavdeck v$Version from $sourceSha in $repository."
    & dotnet restore Cavdeck.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & dotnet build Cavdeck.sln --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet test Cavdeck.sln --no-build -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    $previousStable = @($releases | Where-Object {
        !$_.isPrerelease -and !$_.isDraft -and $_.tagName -match '^v?[0-9]+\.[0-9]+\.[0-9]+$'
    })
    if ($previousStable.Count -gt 0) {
        & vpk download github --repoUrl "https://github.com/$repository" --channel stable --outputDir $releaseDir
        if ($LASTEXITCODE -ne 0) { throw 'Downloading the stable update feed failed.' }
    }

    & (Join-Path $PSScriptRoot 'New-WindowsPackage.ps1') -Version $Version -SkipTests
    if ($LASTEXITCODE -ne 0) { throw 'Windows packaging or verification failed.' }

    # Preserve the standalone CLI ZIP as a separate GitHub Release asset. It is
    # deliberately not added to the Velopack desktop update feed.
    & (Join-Path $PSScriptRoot 'New-WindowsCliPackage.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Standalone CLI packaging or verification failed.' }
    $cliArchiveName = "Cavdeck-Cli-$Version-win-x64.zip"
    $cliArchive = Join-Path $root "artifacts/cli/$cliArchiveName"
    if (!(Test-Path $cliArchive -PathType Leaf)) { throw "Standalone CLI archive is missing: $cliArchiveName" }
    $cliHash = (Get-FileHash $cliArchive -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($DisposableMachine) {
        & (Join-Path $PSScriptRoot 'Test-WindowsInstall.ps1') -ReleasesDirectory $releaseDir -Version $Version -DisposableMachine
        if ($LASTEXITCODE -ne 0) { throw 'Installer smoke test failed.' }
    }
    else {
        Write-Warning 'Installer smoke test skipped. Verify installation, uninstallation and upgrades in a clean disposable VM BEFORE publishing.'
    }

    # Generate user-facing changes from merged pull requests since the previous release.
    # The repository-level .github/release.yml controls category names and label mapping.
    $generatedNotes = (& gh api "repos/$repository/releases/generate-notes" -f "tag_name=$tagName" -f "target_commitish=$sourceSha" --jq .body | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub could not generate release notes. No draft release has been created.'
    }
    if ([string]::IsNullOrWhiteSpace($generatedNotes)) {
        $generatedNotes = 'No categorized pull request summaries were returned for this release.'
    }

    # GitHub's generated body normally includes its own What's Changed heading.
    # Add one only if the API response does not contain a top-level heading.
    if ($generatedNotes -notmatch '^\s*##\s+') {
        $generatedNotes = "## What's Changed`n`n$generatedNotes"
    }

    # Build the complete release description from generated changes and verified build outputs.
    $lines = @(
        $generatedNotes,
        '',
        '## Windows x64 Downloads',
        '',
        '| File | SHA-256 |',
        '| --- | --- |'
    )
    foreach ($name in @("Cavdeck-$Version-Setup.exe", "Cavdeck-$Version.msi")) {
        $installer = Join-Path $releaseDir $name
        if (!(Test-Path $installer -PathType Leaf)) { throw "Missing installer: $name" }
        $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToUpperInvariant()
        $lines += "| $name | $hash |"
    }

    $checksums = Join-Path $releaseDir 'SHA256SUMS.txt'
    if (!(Test-Path $checksums -PathType Leaf)) { throw 'SHA256SUMS.txt is missing.' }
    $checksumEntries = @(Get-Content $checksums | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    $checksumEntries += "$cliHash  $cliArchiveName"
    $checksumEntries |
        Sort-Object { ($_ -split '\s{2}', 2)[1] } |
        Set-Content -Path $checksums -Encoding utf8
    $lines += "| $cliArchiveName | $cliHash |"
    $lines += @(
        '',
        '## Command-line interface',
        '',
        'The Windows installer also installs the Cavdeck CLI. Open a new terminal after installation and run cavdeck --help.',
        '',
        '## Security and Verification',
        '',
        '**These GitHub installers are currently unsigned (not code-signed).** Windows may display an Unknown Publisher or Microsoft Defender SmartScreen warning.',
        '',
        'Download installers only from the [official Cavdeck GitHub Releases](https://github.com/imnbwd/Cavdeck/releases) page and verify their SHA-256 checksums before use.',
        '',
        '**Verify the Setup installer (PowerShell):**',
        '',
        '```powershell',
        "Get-FileHash .\Cavdeck-$Version-Setup.exe -Algorithm SHA256",
        '```',
        '',
        '**Verify the MSI installer (PowerShell):**',
        '',
        '```powershell',
        "Get-FileHash .\Cavdeck-$Version.msi -Algorithm SHA256",
        '```',
        '',
        'Compare the result with the corresponding value above or the matching entry in `SHA256SUMS.txt`.',
        '',
        '',
        '## Automatic Updates',
        '',
        'The `.nupkg` package and `releases.stable.json` feed support Cavdeck''s built-in automatic updater. They are not installers and do not need to be downloaded manually.',
        '',
        '## Build Information',
        '',
        '| Property | Value |',
        '| --- | --- |',
        '| Platform | Windows x64 |',
        "| Source commit | $sourceSha |"
    )
    $notesPath = Join-Path $output 'RELEASE_NOTES.md'
    $lines | Set-Content -Path $notesPath -Encoding utf8

    # Intentionally omit --publish: the review and final publication are manual.
    & vpk upload github --repoUrl "https://github.com/$repository" --channel stable --outputDir $releaseDir --tag "v$Version" --releaseName "Cavdeck v$Version" --targetCommitish $sourceSha
    if ($LASTEXITCODE -ne 0) { throw 'Draft release upload failed.' }

    $draft = & gh release view "v$Version" --repo $repository --json isDraft | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !$draft.isDraft) {
        throw 'The uploaded release is not a draft. Inspect GitHub Releases immediately.'
    }
    & gh release upload "v$Version" $checksums $cliArchive --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Checksum and standalone CLI upload failed. Inspect the draft.' }
    & gh release edit "v$Version" --repo $repository --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw 'Release notes update failed. Inspect the draft.' }

    Write-Host "Draft ready: https://github.com/$repository/releases/tag/v$Version"
    Write-Host 'Publish manually after clean-VM and two-version-upgrade checks.'
}
finally {
    $env:GH_TOKEN = $previousGhToken
    $env:VPK_TOKEN = $previousVpkToken
    Pop-Location
}
