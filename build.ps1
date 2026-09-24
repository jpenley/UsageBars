<#
    Publishes UsageBars as a single self-contained exe in dist\.
    No .NET runtime is required on the machine that runs it.

    A running instance is stopped before publishing (it holds a lock on the exe) and relaunched
    afterwards if it was running. Use -NoRelaunch to leave it stopped, or -Run to start it even
    if it wasn't running.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # Publish but don't start the app again, even if it was running.
    [switch]$NoRelaunch,

    # Start the app afterwards whether or not it was running.
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\UsageBars.csproj'
$dist = Join-Path $root 'dist'
$staging = Join-Path $root 'src\bin\publish'
$distExe = Join-Path $dist 'UsageBars.exe'
$stagingExe = Join-Path $staging 'UsageBars.exe'

# Instances of THIS build only, matched by executable path.
#
# Matching on the process name alone would be wrong and destructive: the older UsageBars project
# produces an executable with the same name, and on this machine it is registered to start with
# Windows. Killing an unrelated app because it shares a name is not this script's business.
function Get-OwnInstances {
    param([string[]] $Paths)

    $wanted = @($Paths | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })

    Get-Process -Name 'UsageBars' -ErrorAction SilentlyContinue | Where-Object {
        try { $wanted -contains $_.MainModule.FileName.ToLowerInvariant() }
        catch { $false }   # another user's process, or exited mid-enumeration
    }
}

$ownPaths = @($distExe, $stagingExe)
$running = @(Get-OwnInstances -Paths $ownPaths)
$wasRunning = $running.Count -gt 0

if ($wasRunning) {
    Write-Host "stopping $($running.Count) running instance(s)..."
    foreach ($process in $running) {
        try { $process.CloseMainWindow() | Out-Null } catch { }
    }

    # The pill is a tool window with no real main window, so a graceful close usually does
    # nothing. Give it a moment anyway, then insist.
    foreach ($process in $running) {
        try {
            if (-not $process.WaitForExit(2000)) { $process.Kill() }
            $null = $process.WaitForExit(5000)
        } catch { }
    }

    # The publish overwrites dist\UsageBars.exe, which stays locked briefly after exit.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-OwnInstances -Paths $ownPaths).Count -gt 0) {
        Start-Sleep -Milliseconds 200
    }
    if (@(Get-OwnInstances -Paths $ownPaths).Count -gt 0) {
        throw "a UsageBars instance from this build is still running; close it and retry"
    }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

dotnet publish $project -c Release -r $Runtime --self-contained true -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

if (-not (Test-Path $stagingExe)) { throw "expected $stagingExe to exist after publish" }

Copy-Item $stagingExe $distExe -Force

# Ship an editable config beside the exe. Never overwrite one the user has tuned.
$configPath = Join-Path $dist 'UsageBars.config.json'
if (Test-Path $configPath) {
    Write-Host "kept existing dist\UsageBars.config.json"
} else {
    Copy-Item (Join-Path $root 'UsageBars.config.default.json') $configPath
    Write-Host "wrote default dist\UsageBars.config.json"
}

$size = [math]::Round((Get-Item $distExe).Length / 1MB, 1)
Write-Host ""
Write-Host "dist\UsageBars.exe  ($size MB, $Runtime, self-contained)"
Write-Host "dist\UsageBars.config.json  (optional - every setting has a working default)"

if ($Run -or ($wasRunning -and -not $NoRelaunch)) {
    Start-Process -FilePath $distExe | Out-Null
    Write-Host ""
    Write-Host $(if ($wasRunning) { "relaunched (it was running before the build)" } else { "started" })
} elseif ($wasRunning) {
    Write-Host ""
    Write-Host "left stopped (-NoRelaunch)"
}
