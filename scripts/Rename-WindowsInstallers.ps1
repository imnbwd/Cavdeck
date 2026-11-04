param(
    [Parameter(Mandatory = $true)] [string]$ReleasesDirectory,
    [Parameter(Mandatory = $true)] [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')] [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releases = (Resolve-Path $ReleasesDirectory).Path
$assetsPath = Join-Path $releases 'assets.stable.json'
if (!(Test-Path $assetsPath -PathType Leaf)) { throw 'Velopack assets manifest is missing.' }
$assets = @(Get-Content $assetsPath -Raw | ConvertFrom-Json)

$names = @{
    Installer = @('Cavdeck-stable-Setup.exe', "Cavdeck-$Version-Setup.exe")
    Msi = @('Cavdeck-stable.msi', "Cavdeck-$Version.msi")
}
foreach ($type in $names.Keys) {
    $matching = @($assets | Where-Object { $_.Type -eq $type })
    if ($matching.Count -ne 1 -or $matching[0].RelativeFileName -ne $names[$type][0]) {
        throw "Unexpected Velopack $type asset; installer naming has changed."
    }
    $source = Join-Path $releases $names[$type][0]
    $target = Join-Path $releases $names[$type][1]
    if (!(Test-Path $source -PathType Leaf) -or (Test-Path $target)) {
        throw "Cannot rename $type installer safely."
    }
}

foreach ($type in $names.Keys) {
    Move-Item -LiteralPath (Join-Path $releases $names[$type][0]) `
        -Destination (Join-Path $releases $names[$type][1])
    $asset = @($assets | Where-Object { $_.Type -eq $type })[0]
    $asset.RelativeFileName = $names[$type][1]
}
$assets | ConvertTo-Json -Depth 5 -Compress | Set-Content -Path $assetsPath -Encoding utf8
Write-Host "Installer filenames ready for download: Cavdeck-$Version-Setup.exe, Cavdeck-$Version.msi"
