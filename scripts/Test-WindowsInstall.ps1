param(
    [Parameter(Mandatory = $true)] [string]$ReleasesDirectory,
    [Parameter(Mandatory = $true)] [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')] [string]$Version,
    [switch]$RequireSignature,
    [switch]$DisposableMachine
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$DisposableMachine) {
    throw 'Run this installer smoke test only on a disposable Windows runner or VM, with -DisposableMachine.'
}

# Setup.exe uses a fixed per-user app identity; smoke tests must start from a clean machine.
if (Get-Process -Name 'Cavdeck' -ErrorAction SilentlyContinue) {
    throw 'Cavdeck is already running. Use a clean disposable runner or VM.'
}
if (Test-Path (Join-Path $env:LOCALAPPDATA 'Cavdeck')) {
    throw 'An existing Cavdeck application directory was found. Use a clean disposable runner or VM.'
}
foreach ($key in @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)) {
    $existing = @(Get-ChildItem -Path $key -ErrorAction SilentlyContinue |
        Get-ItemProperty -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Cavdeck'
        })
    if ($existing.Count -gt 0) {
        throw 'An existing Cavdeck registration was found. Use a clean disposable runner or VM.'
    }
}
$releases = (Resolve-Path $ReleasesDirectory).Path
$setups = @(Get-ChildItem $releases -Filter "Cavdeck-$Version-Setup.exe" -File)
if ($setups.Count -ne 1) { throw 'Expected exactly one Setup.exe.' }
$msis = @(Get-ChildItem $releases -Filter "Cavdeck-$Version.msi" -File)
if ($msis.Count -ne 1) { throw 'Expected exactly one MSI.' }

# Keep diagnostic logs outside the Velopack release feed.
$logs = Join-Path (Split-Path $releases -Parent) 'smoke-logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
Write-Host "Installer smoke-test logs: $logs"

# Verify the standard per-user installation location used by Velopack Setup.exe.
$install = Join-Path $env:LOCALAPPDATA 'Cavdeck'
$setupLog = Join-Path $logs 'setup-install.log'
$setup = Start-Process -FilePath $setups[0].FullName -ArgumentList @(
    '--silent',
    '--log', ('"{0}"' -f $setupLog)
) -Wait -PassThru
if ($setup.ExitCode -ne 0) {
    Write-Warning "Setup may have left files at $install."
    throw "Silent installation failed with exit code $($setup.ExitCode). See $setupLog"
}

$updater = Join-Path $install 'Update.exe'
$app = Join-Path $install 'current/Cavdeck.exe'
if (!(Test-Path $updater -PathType Leaf) -or !(Test-Path $app -PathType Leaf)) {
    throw "Installation did not create the updater and app in $install."
}
try {
    if ($RequireSignature) {
        foreach ($file in @($updater, $app)) {
            $signature = Get-AuthenticodeSignature -FilePath $file
            if ($signature.Status -ne 'Valid') { throw "Installed binary is not trusted: $file ($($signature.Status))." }
        }
    }
    $uninstallLog = Join-Path $logs 'setup-uninstall.log'
    $uninstall = Start-Process -FilePath $updater -ArgumentList @(
        '--silent', '--log', ('"{0}"' -f $uninstallLog), 'uninstall'
    ) -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Silent uninstall failed with exit code $($uninstall.ExitCode). See $uninstallLog"
    }
    if (Test-Path $install) { throw "Uninstall left the install directory in place: $install" }
    Write-Host 'Setup.exe silent install and uninstall passed.'
}
catch {
    Write-Warning "Smoke installation remains at $install. Inspect it before cleaning up."
    throw
}

$msiInstall = Join-Path ([IO.Path]::GetTempPath()) ('cavdeck-msi-smoke-' + [guid]::NewGuid().ToString('N'))
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msis[0].FullName, 0)
$view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property`=''ProductCode''')
$view.Execute()
$record = $view.Fetch()
if (!$record) { throw 'MSI ProductCode is missing.' }
$productCode = $record.StringData(1)
$view.Close()
$msiLog = Join-Path $logs 'msi-install.log'
$msiArgs = '/i "{0}" /qn /norestart ALLUSERS=2 MSIINSTALLPERUSER=1 VELOPACK_INSTALLDIR="{1}" /L*v "{2}"' -f $msis[0].FullName, $msiInstall, $msiLog
$msiResult = Start-Process msiexec.exe -ArgumentList $msiArgs -Wait -PassThru
if ($msiResult.ExitCode -ne 0) {
    throw "MSI silent installation failed with exit code $($msiResult.ExitCode). See $msiLog"
}
try {
    $msiApp = Join-Path $msiInstall 'current/Cavdeck.exe'
    if (!(Test-Path $msiApp -PathType Leaf)) { throw "MSI did not install Cavdeck.exe in $msiInstall." }
    if ($RequireSignature) {
        $signature = Get-AuthenticodeSignature -FilePath $msiApp
        if ($signature.Status -ne 'Valid') { throw "MSI-installed app is not trusted: $($signature.Status)." }
    }
    Write-Host 'MSI silent installation passed.'
}
finally {
    $msiUninstallLog = Join-Path $logs 'msi-uninstall.log'
    $remove = Start-Process msiexec.exe -ArgumentList ('/x {0} /qn /norestart /L*v "{1}"' -f $productCode, $msiUninstallLog) -Wait -PassThru
    if ($remove.ExitCode -ne 0) {
        throw "MSI silent uninstall failed with exit code $($remove.ExitCode). See $msiUninstallLog"
    }
    if (Test-Path $msiInstall) { throw "MSI uninstall left the installation directory: $msiInstall" }
    Write-Host 'MSI silent uninstall passed.'
}
