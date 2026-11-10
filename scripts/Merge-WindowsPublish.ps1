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
if (Test-Path $output) { throw "Merged publish directory already exists: $output" }

New-Item -ItemType Directory -Force -Path $output | Out-Null

function Copy-PublishFiles([string]$Source, [string]$Destination) {
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

        if (Test-Path $target -PathType Leaf) {
            $existingHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            $incomingHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($existingHash -ne $incomingHash) {
                throw "Desktop and CLI publish outputs contain conflicting files: $relative"
            }
            continue
        }

        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

Copy-PublishFiles -Source $desktop -Destination $output
Copy-PublishFiles -Source $cli -Destination $output

$requiredFiles = @(
    'Cavdeck.exe',
    'Cavdeck.dll',
    'Cavdeck.deps.json',
    'Cavdeck.runtimeconfig.json',
    'cavdeck-cli.exe',
    'cavdeck-cli.dll',
    'cavdeck-cli.deps.json',
    'cavdeck-cli.runtimeconfig.json'
)
foreach ($name in $requiredFiles) {
    if (!(Test-Path (Join-Path $output $name) -PathType Leaf)) {
        throw "Merged publish output is missing $name."
    }
}

Write-Host "Merged Desktop and CLI publish outputs into $output."
