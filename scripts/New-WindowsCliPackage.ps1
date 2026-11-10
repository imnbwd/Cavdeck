param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [string]$OutputDirectory = "artifacts/cli"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Cavdeck CLI packages require Windows.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (!$output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be beneath the repository artifacts directory.'
}
$publish = Join-Path $output 'win-x64'
$archiveName = "Cavdeck-Cli-$Version-win-x64.zip"
$archivePath = Join-Path $output $archiveName
if (Test-Path $publish) { throw "CLI publish directory already exists: $publish" }
if (Test-Path $archivePath) { throw "CLI archive already exists: $archivePath" }

Push-Location $root
try {
    $arguments = @('publish', 'src/Cavdeck.Cli/Cavdeck.Cli.csproj', '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', '-p:PublishTrimmed=false',
        "-p:Version=$Version", '-o', $publish, '-m:1')
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Standalone CLI publish failed.' }

    $exe = Join-Path $publish 'cavdeck.exe'
    if (!(Test-Path $exe -PathType Leaf)) { throw 'CLI executable is missing.' }

    $reportedVersion = (& $exe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($reportedVersion -split '\+', 2)[0] -ne $Version) {
        throw "CLI version mismatch: expected $Version, received $reportedVersion."
    }

    $helpText = (& $exe --help | Out-String)
    if ($LASTEXITCODE -ne 0 -or $helpText -notmatch 'Usage:' -or $helpText -notmatch 'Options:') {
        throw 'The published CLI could not display English help.'
    }

    # Ship a production CLI without debug symbols while preserving all runtime dependencies.
    Get-ChildItem -Path $publish -Filter '*.pdb' -Recurse -File | Remove-Item -Force
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archivePath -CompressionLevel Optimal
    if (!(Test-Path $archivePath -PathType Leaf)) { throw 'CLI ZIP was not created.' }

    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        foreach ($required in @('cavdeck.exe', 'cavdeck.dll', 'cavdeck.runtimeconfig.json')) {
            if ($names -cnotcontains $required) { throw "CLI ZIP is missing $required." }
        }
        if (@($names | Where-Object { $_ -match '(?i)\.pdb$' }).Count) {
            throw 'Debug symbols entered the CLI ZIP.'
        }
    }
    finally { $archive.Dispose() }
    $checksum = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "CLI package ready: $archivePath (SHA-256: $checksum)"
}
finally {
    Pop-Location
}
