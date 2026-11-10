param(
    [Parameter(Mandatory = $true)] [string]$DesktopDirectory,
    [Parameter(Mandatory = $true)] [string]$CliDirectory,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$desktop = (Resolve-Path $DesktopDirectory).Path
$cli = (Resolve-Path $CliDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw "Assembled publish directory already exists: $output" }

New-Item -ItemType Directory -Force -Path $output | Out-Null
$cliTarget = Join-Path $output 'cli'
New-Item -ItemType Directory -Force -Path $cliTarget | Out-Null

function Copy-PublishTree([string]$Source, [string]$Destination) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse -Force | Sort-Object FullName) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publish output contains a reparse point: $($file.FullName)"
        }

        $relative = $file.FullName.Substring($Source.Length).TrimStart([char[]]@([char]92, [char]47))
        $target = Join-Path $Destination $relative
        $targetDirectory = Split-Path -Parent $target
        if (!(Test-Path $targetDirectory)) {
            New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
        }

        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

# Desktop and CLI intentionally keep isolated dependency graphs. Their self-contained
# Windows publishes can contain different versions of identically named assemblies
# (for example WindowsBase.dll), so they must not share an application base directory.
Copy-PublishTree -Source $desktop -Destination $output
Copy-PublishTree -Source $cli -Destination $cliTarget

$requiredFiles = @(
    'Cavdeck.exe',
    'Cavdeck.dll',
    'Cavdeck.deps.json',
    'Cavdeck.runtimeconfig.json'
)
foreach ($name in $requiredFiles) {
    if (!(Test-Path (Join-Path $output $name) -PathType Leaf)) {
        throw "Desktop publish output is missing $name."
    }
}

$requiredCliFiles = @(
    'cavdeck.exe',
    'cavdeck.dll',
    'cavdeck.deps.json',
    'cavdeck.runtimeconfig.json'
)
foreach ($name in $requiredCliFiles) {
    if (!(Test-Path (Join-Path $cliTarget $name) -PathType Leaf)) {
        throw "CLI publish output is missing cli/$name."
    }
}

Write-Host "Assembled isolated Desktop and CLI outputs under $output."
