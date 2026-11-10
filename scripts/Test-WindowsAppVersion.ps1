param(
    [Parameter(Mandatory = $true)] [string]$AppDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [ValidateSet('Desktop', 'Cli')]
    [string]$ApplicationKind = 'Desktop'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$app = (Resolve-Path $AppDirectory).Path
$expectedFileVersion = "$Version.0"
$sharedAssemblies = @('Cavdeck.Application.dll', 'Cavdeck.Domain.dll', 'Cavdeck.Infrastructure.dll')

if ($ApplicationKind -eq 'Desktop') {
    $expectedExecutables = @('Cavdeck.exe')
    $expectedAssemblies = @('Cavdeck.dll') + $sharedAssemblies
}
else {
    $expectedExecutables = @('cavdeck-cli.exe')
    $expectedAssemblies = @('cavdeck-cli.dll') + $sharedAssemblies
}

foreach ($name in $expectedExecutables + $expectedAssemblies) {
    $path = Join-Path $app $name
    if (!(Test-Path $path -PathType Leaf)) { throw "Published app is missing $name." }

    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($info.FileVersion -ne $expectedFileVersion) {
        throw "$name file version is $($info.FileVersion); expected $expectedFileVersion."
    }

    # Source Link may append +commit to the informational version shown as ProductVersion.
    if (($info.ProductVersion -split '\+', 2)[0] -ne $Version) {
        throw "$name product version is $($info.ProductVersion); expected $Version."
    }
}

foreach ($name in $expectedAssemblies) {
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $app $name)).Version.ToString()
    if ($assemblyVersion -ne $expectedFileVersion) {
        throw "$name assembly version is $assemblyVersion; expected $expectedFileVersion."
    }
}

Write-Host "Verified Cavdeck $ApplicationKind app, file and assembly versions: $Version."
