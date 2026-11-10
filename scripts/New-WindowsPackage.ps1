param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [string]$OutputDirectory = "artifacts/windows-package/$Version",
    [switch]$RequireSignature,
    [switch]$SkipTests,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows Installer uses the first three version fields for upgrade comparisons.
try { $msiVersion = [version]::Parse($Version) }
catch { throw "Invalid Windows package version: $Version" }
if ($msiVersion.Major -gt 255 -or $msiVersion.Minor -gt 255 -or
        $msiVersion.Build -gt 65535) {
    throw "Version $Version exceeds the Windows Installer limit of 255.255.65535."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (!$output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be beneath the repository artifacts directory.'
}
$publish = Join-Path $output 'publish'
$staging = Join-Path $output 'staging'
$desktopPublish = Join-Path $staging 'desktop'
$cliPublish = Join-Path $staging 'cli'
$releases = Join-Path $output 'Releases'
$artwork = Join-Path $output 'installer-artwork'
if (Test-Path $publish) { throw "Publish directory already exists: $publish" }
if (Test-Path $staging) { throw "Staging directory already exists: $staging" }

Push-Location $root
try {
    if (!(Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw 'Velopack CLI vpk 1.2.161 is required. Install it with: dotnet tool install --global vpk --version 1.2.161'
    }
    $vpkVersion = (& vpk -H | Select-Object -First 3) -join ' '
    if ($vpkVersion -notmatch '1\.2\.161') {
        throw "Expected Velopack CLI 1.2.161; found: $vpkVersion"
    }
    if ($RequireSignature -and [string]::IsNullOrWhiteSpace($env:VPK_SIGN_PARAMS)) {
        throw 'VPK_SIGN_PARAMS is required for a signed package.'
    }

    if (!$SkipTests) {
        $testArgs = @('test', 'Cavdeck.sln', '-c', 'Release', '-m:1')
        if ($NoRestore) { $testArgs += '--no-restore' }
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    }
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    # Publish independently so each app retains its own deps/runtime config and apphost.
    # Merge only byte-identical shared files; conflicting dependency versions fail the build.
    $desktopPublishArgs = @('publish', 'src/Cavdeck.App/Cavdeck.App.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", '-o', $desktopPublish, '-m:1')
    if ($NoRestore) { $desktopPublishArgs += '--no-restore' }
    & dotnet @desktopPublishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Desktop Windows publish failed.' }

    $cliPublishArgs = @('publish', 'src/Cavdeck.Cli/Cavdeck.Cli.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', '-p:PublishTrimmed=false',
        "-p:Version=$Version", '-o', $cliPublish, '-m:1')
    if ($NoRestore) { $cliPublishArgs += '--no-restore' }
    & dotnet @cliPublishArgs
    if ($LASTEXITCODE -ne 0) { throw 'CLI Windows publish failed.' }

    if (!(Test-Path (Join-Path $desktopPublish 'Cavdeck.exe') -PathType Leaf)) {
        throw 'Desktop publish is missing Cavdeck.exe.'
    }
    if (!(Test-Path (Join-Path $cliPublish 'cavdeck-cli.exe') -PathType Leaf)) {
        throw 'CLI publish is missing cavdeck-cli.exe.'
    }

    & (Join-Path $PSScriptRoot 'Merge-WindowsPublish.ps1') -DesktopDirectory $desktopPublish -CliDirectory $cliPublish -OutputDirectory $publish
    if ($LASTEXITCODE -ne 0) { throw 'Desktop and CLI publish merge failed.' }
    Remove-Item -LiteralPath $staging -Recurse -Force

    & (Join-Path $PSScriptRoot 'Test-WindowsAppVersion.ps1') -AppDirectory $publish -Version $Version

    $cli = Join-Path $publish 'cavdeck-cli.exe'
    $cliVersion = (& $cli --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($cliVersion -split '\+', 2)[0] -ne $Version) {
        throw "Published CLI version mismatch: expected $Version, received $cliVersion."
    }
    $cliHelp = (& $cli --help | Out-String)
    if ($LASTEXITCODE -ne 0 -or $cliHelp -notmatch 'Usage:' -or $cliHelp -notmatch 'Options:') {
        throw 'The published CLI could not display help.'
    }

    & (Join-Path $PSScriptRoot 'New-InstallerArtwork.ps1') `
        -IconPath 'src/Cavdeck.App/Assets/Cavdeck.ico' -OutputDirectory $artwork

    & vpk pack --packId Cavdeck --packTitle Cavdeck --packVersion $Version `
        --packDir $publish --mainExe Cavdeck.exe --runtime win-x64 --channel stable `
        --noPortable --msi --instLocation PerUser `
        --exclude '.*(\.pdb|Microsoft\.Web\.WebView2\.[^\\/]*\.xml)$' `
        --icon src/Cavdeck.App/Assets/Cavdeck.ico `
        --splashImage (Join-Path $artwork 'CavdeckSetupSplash.png') `
        --splashProgressColor '#2563EB' `
        --msiTopBanner (Join-Path $artwork 'CavdeckMsiBanner.bmp') `
        --msiDialogBackground (Join-Path $artwork 'CavdeckMsiDialog.bmp') `
        --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }

    & (Join-Path $PSScriptRoot 'Rename-WindowsInstallers.ps1') `
        -ReleasesDirectory $releases -Version $Version

    & (Join-Path $PSScriptRoot 'Test-WindowsPackage.ps1') -ReleasesDirectory $releases `
        -Version $Version -ArtworkDirectory $artwork -RequireSignature:$RequireSignature
    if ($LASTEXITCODE -ne 0) { throw 'Package verification failed.' }
    Write-Host "Package ready: $releases"
}
finally {
    Pop-Location
}
