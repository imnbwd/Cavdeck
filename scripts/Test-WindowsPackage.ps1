param(
    [Parameter(Mandatory = $true)] [string]$ReleasesDirectory,
    [Parameter(Mandatory = $true)] [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')] [string]$Version,
    [string]$ArtworkDirectory,
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releases = (Resolve-Path $ReleasesDirectory).Path
$feedPath = Join-Path $releases 'releases.stable.json'
if (!(Test-Path $feedPath -PathType Leaf)) { throw 'Stable update feed is missing.' }
$feed = Get-Content $feedPath -Raw | ConvertFrom-Json
$currentAssets = @($feed.Assets | Where-Object { $_.Version -eq $Version })
if ($currentAssets.Count -lt 1) { throw "Stable feed has no assets for version $Version." }
$currentFull = @($currentAssets | Where-Object {
    $_.PackageId -eq 'Cavdeck' -and $_.Type -eq 'Full' -and
    $_.FileName -eq "Cavdeck-$Version-stable-full.nupkg"
})
if ($currentFull.Count -ne 1) { throw 'Stable feed has no matching full Cavdeck package.' }

$setups = @(Get-ChildItem $releases -Filter "Cavdeck-$Version-Setup.exe" -File)
$msis = @(Get-ChildItem $releases -Filter "Cavdeck-$Version.msi" -File)
$fullPackages = @(Get-ChildItem $releases -Filter '*-full.nupkg' -File)
if ($setups.Count -ne 1 -or $setups[0].Name -ne "Cavdeck-$Version-Setup.exe") {
    throw 'Expected the versioned Cavdeck Setup.exe installer.'
}
if ($msis.Count -ne 1 -or $msis[0].Name -ne "Cavdeck-$Version.msi") {
    throw 'Expected the versioned Cavdeck MSI installer.'
}
$assetsPath = Join-Path $releases 'assets.stable.json'
if (!(Test-Path $assetsPath -PathType Leaf)) { throw 'Velopack upload manifest is missing.' }
$uploadAssets = @(Get-Content $assetsPath -Raw | ConvertFrom-Json)
foreach ($expected in @(@('Installer', $setups[0].Name), @('Msi', $msis[0].Name),
        @('Full', "Cavdeck-$Version-stable-full.nupkg"))) {
    $matching = @($uploadAssets | Where-Object {
        $_.Type -eq $expected[0] -and $_.RelativeFileName -eq $expected[1]
    })
    if ($matching.Count -ne 1) { throw "Velopack upload manifest does not match $($expected[1])." }
}
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$msiDatabase = $windowsInstaller.OpenDatabase($msis[0].FullName, 0)
$dialogView = $msiDatabase.OpenView('SELECT `Dialog` FROM `Dialog`')
$dialogView.Execute()
$dialogs = @()
while ($dialogRecord = $dialogView.Fetch()) { $dialogs += $dialogRecord.StringData(1) }
$dialogView.Close()
if ($dialogs -notcontains 'WelcomeDlg') {
    throw 'MSI installation wizard is missing.'
}
$versionView = $msiDatabase.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'")
$versionView.Execute()
$versionRecord = $versionView.Fetch()
$msiVersion = if ($versionRecord) { $versionRecord.StringData(1) } else { $null }
$versionView.Close()
if ($msiVersion -ne "$Version.0") {
    throw "MSI ProductVersion is $msiVersion; expected $Version.0."
}
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setups[0].FullName)
# Source Link may append +commit to the informational version shown as ProductVersion.
$setupProductVersion = ($setupInfo.ProductVersion -split '\+', 2)[0]
if ($setupInfo.FileVersion -ne $Version -or $setupProductVersion -ne $Version) {
    throw "Setup.exe version is $($setupInfo.FileVersion) / $($setupInfo.ProductVersion); expected $Version."
}
if ($ArtworkDirectory) {
    $artwork = (Resolve-Path $ArtworkDirectory).Path
    $expectedArtwork = @{
        WixUI_Bmp_Banner = Join-Path $artwork 'CavdeckMsiBanner.bmp'
        WixUI_Bmp_Dialog = Join-Path $artwork 'CavdeckMsiDialog.bmp'
    }
    if (!(Test-Path (Join-Path $artwork 'CavdeckSetupSplash.png') -PathType Leaf)) {
        throw 'Cavdeck Setup splash image is missing.'
    }
    $binaryView = $msiDatabase.OpenView('SELECT `Name`, `Data` FROM `Binary`')
    $binaryView.Execute()
    $embeddedSizes = @{}
    while ($binaryRecord = $binaryView.Fetch()) {
        $embeddedSizes[$binaryRecord.StringData(1)] = $binaryRecord.DataSize(2)
    }
    $binaryView.Close()
    foreach ($name in $expectedArtwork.Keys) {
        $expectedBytes = (Get-Item $expectedArtwork[$name]).Length
        if ($embeddedSizes[$name] -ne $expectedBytes) {
            throw "MSI did not embed the Cavdeck artwork: $name"
        }
    }
}
if ($fullPackages.Count -lt 1) { throw 'A full update package is missing.' }
if (!(@($fullPackages | Where-Object { $_.Name -eq "Cavdeck-$Version-stable-full.nupkg" }).Count)) {
    throw 'Installer name or full package version does not match the expected Velopack output.'
}
foreach ($asset in $currentAssets) {
    $file = Join-Path $releases $asset.FileName
    if (!(Test-Path $file -PathType Leaf)) { throw "Feed asset is missing: $($asset.FileName)" }
    if ((Get-Item $file).Length -ne $asset.Size) { throw "Feed asset size differs: $($asset.FileName)" }
    if ($asset.SHA1 -notmatch '^[0-9a-fA-F]{40}$' -and
            $asset.SHA256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Feed asset has no valid checksum: $($asset.FileName)"
    }
    if ($asset.SHA1 -and (Get-FileHash $file -Algorithm SHA1).Hash -ne $asset.SHA1) {
        throw "Feed asset checksum differs: $($asset.FileName)"
    }
    if ($asset.SHA256 -and (Get-FileHash $file -Algorithm SHA256).Hash -ne $asset.SHA256) {
        throw "Feed asset SHA-256 differs: $($asset.FileName)"
    }
}
if ($RequireSignature) {
    foreach ($installer in @($setups[0], $msis[0])) {
        $signature = Get-AuthenticodeSignature -FilePath $installer.FullName
        if ($signature.Status -ne 'Valid') {
            throw "Installer Authenticode signature is not trusted: $($installer.Name) ($($signature.Status))."
        }
    }
}

Add-Type -AssemblyName System.IO.Compression
$package = [IO.Compression.ZipFile]::OpenRead(
    (Join-Path $releases "Cavdeck-$Version-stable-full.nupkg"))
try {
    $packagedFiles = @($package.Entries | ForEach-Object FullName)
    if ($packagedFiles -notcontains 'lib/app/Cavdeck.exe') { throw 'Cavdeck.exe is missing from the package.' }
    foreach ($required in @(
            'lib/app/Cavdeck.runtimeconfig.json',
            'lib/app/cli/cavdeck.exe',
            'lib/app/cli/cavdeck.dll',
            'lib/app/cli/cavdeck.deps.json',
            'lib/app/cli/cavdeck.runtimeconfig.json')) {
        if ($packagedFiles -notcontains $required) {
            throw "Unified package is missing $required."
        }
    }
    if ($packagedFiles -notcontains 'lib/app/coreclr.dll' -or
            $packagedFiles -notcontains 'lib/app/cli/coreclr.dll') {
        throw 'A self-contained .NET runtime is missing from the Desktop or CLI package.'
    }
    if ($packagedFiles -notcontains 'lib/app/cli/e_sqlite3.dll' -and
            $packagedFiles -notcontains 'lib/app/cli/runtimes/win-x64/native/e_sqlite3.dll') {
        throw 'The CLI SQLite native library is missing.'
    }
    if ($packagedFiles -notcontains 'lib/app/e_sqlite3.dll') {
        throw 'The Windows x64 SQLite native library is missing.'
    }
    $wrongPlatform = @($packagedFiles | Where-Object {
        $_ -match '(?i)^lib/app/(cli/)?runtimes/(linux|osx|macos|android|ios|win-(x86|arm|arm64))/' -or
        $_ -match '(?i)^lib/app/(libe_sqlite3|e_sqlite3\.(so|dylib))'
    })
    if ($wrongPlatform.Count) {
        throw "Non-Windows-x64 native files entered the package: $($wrongPlatform -join ', ')"
    }
    $unexpected = @($packagedFiles | Where-Object {
        $_ -match '(?i)^lib/app/.*(\.pdb$|Microsoft\.Web\.WebView2\.[^/]*\.xml$|\.env|[\\/]tests?[\\/]|DesignCatalog)'
    })
    if ($unexpected.Count) { throw "Development files entered the package: $($unexpected -join ', ')" }

    $versionProbe = Join-Path ([IO.Path]::GetTempPath()) ('cavdeck-version-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $versionProbe | Out-Null
    try {
        foreach ($name in @('Cavdeck.exe', 'Cavdeck.dll', 'Cavdeck.Application.dll',
                'Cavdeck.Domain.dll', 'Cavdeck.Infrastructure.dll')) {
            $entry = $package.GetEntry("lib/app/$name")
            if (!$entry) { throw "Full package is missing $name." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $versionProbe $name))
        }

        $cliProbe = Join-Path $versionProbe 'cli'
        New-Item -ItemType Directory -Path $cliProbe | Out-Null
        foreach ($name in @('cavdeck.exe', 'cavdeck.dll', 'Cavdeck.Application.dll',
                'Cavdeck.Domain.dll', 'Cavdeck.Infrastructure.dll')) {
            $entry = $package.GetEntry("lib/app/cli/$name")
            if (!$entry) { throw "Full package is missing cli/$name." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $cliProbe $name))
        }

        & (Join-Path $PSScriptRoot 'Test-WindowsAppVersion.ps1') -AppDirectory $versionProbe -Version $Version -ApplicationKind Desktop
        & (Join-Path $PSScriptRoot 'Test-WindowsAppVersion.ps1') -AppDirectory $cliProbe -Version $Version -ApplicationKind Cli
    }
    finally { Remove-Item -LiteralPath $versionProbe -Recurse -Force }
}
finally { $package.Dispose() }

$hashes = Get-ChildItem $releases -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
$hashes | Set-Content -Path (Join-Path $releases 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Verified Setup.exe, MSI, stable feed, full package and SHA-256 manifest."
