# DshShell smoke test
# Builds, launches an ISOLATED shell, and checks the startup handshake in that
# shell's own log: engine resolve -> ready -> splash ack -> app page loaded.
#
# Usage: pwsh .\test.ps1 [-SkipBuild] [-TimeoutSeconds 40]
#
# The launched shell is isolated through four environment variables the app and
# the engine read (all opt-in; unset means normal behaviour):
#   DSHSHELL_LOG          -> its own log file, so no shared log is truncated
#   DSHSHELL_INSTANCE     -> its own single-instance mutex/event, so it neither
#                            collides with nor signals a shell you are using
#   DSHSHELL_WEBVIEW2_DIR -> its own browser profile, since two WebView2
#                            instances cannot share one user-data folder
#   DSH_HOME              -> its own harness home. The user's ~/.dsh profile
#                            takes a single-owner ledger lock (ui-task-board),
#                            so a second engine against the same home aborts
#                            with "task-board ledger is already owned by
#                            process N" instead of booting.
#
# Without DSHSHELL_INSTANCE the app treats this launch as a second instance,
# signals the running one to come to the front, and exits - and when that
# running shell is the GUI hosting this terminal, the signal tears the session
# down mid-run. The test therefore NEVER closes an instance it did not launch,
# and never deletes a log it did not create.
param(
    [switch]$SkipBuild,
    [int]$TimeoutSeconds = 40
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$exe = Join-Path $root "bin\Release\net10.0-windows10.0.17763.0\DshShell.exe"

function Fail($msg) { Write-Host "FAIL: $msg" -ForegroundColor Red; exit 1 }
function Pass($msg) { Write-Host "  ok  $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  warn  $msg" -ForegroundColor Yellow }

if (-not $SkipBuild) {
    Write-Host "==> Building ..."
    dotnet build (Join-Path $root "DshShell.csproj") -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "build failed" }
}

if (-not (Test-Path $exe)) { Fail "missing $exe" }

# Everything this run creates lives under one directory, removed on success.
$runDir  = Join-Path ([System.IO.Path]::GetTempPath()) ("DshShell-test-" + $PID)
$log     = Join-Path $runDir "dsh-shell.log"
$profile = Join-Path $runDir "WebView2"
$dshHome = Join-Path $runDir ".dsh"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

# Start-Process children inherit this process's environment.
$env:DSHSHELL_LOG = $log
$env:DSHSHELL_INSTANCE = "test-$PID"
$env:DSHSHELL_WEBVIEW2_DIR = $profile
$env:DSH_HOME = $dshHome

# Engines belonging to whatever shell is already running. The isolated run must
# not leave anything beyond this set once it has been closed.
function Get-EnginePids {
    @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'DshShell\\engine' } | ForEach-Object { $_.ProcessId })
}
$enginesBefore = Get-EnginePids

$launched = $null
$loaded = $false
try {
    Write-Host "==> Launching an isolated shell ..."
    Write-Host "    log: $log"
    $launched = Start-Process -FilePath $exe -PassThru

    Write-Host "==> Waiting for the app page (max ${TimeoutSeconds}s) ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern "App page loaded" -Quiet)) { $loaded = $true; break }
        if ($launched.HasExited) { break }
    }

    $text = if (Test-Path $log) { Get-Content $log -Raw } else { "" }

    Write-Host "==> Closing the shell this test launched (pid $($launched.Id)) ..."
    if (-not $launched.HasExited) {
        try { $launched.CloseMainWindow() | Out-Null } catch { }
        # Only force it down if it refuses to close on its own.
        if (-not $launched.WaitForExit(8000)) {
            Warn "instance did not close on its own - forcing it down."
            try { $launched.Kill() } catch { }
            $launched.WaitForExit(5000) | Out-Null
        }
    }
    Start-Sleep -Milliseconds 500
    $stillRunning = -not $launched.HasExited
    if ($stillRunning) {
        try { $launched.Kill() } catch { }
        $launched.WaitForExit(5000) | Out-Null
        $stillRunning = -not $launched.HasExited
    }

    Write-Host "==> Checks:"
    if ($text -match "Engine via (dedicated dir|PATH|npx cache)") { Pass "engine resolved" } else { Fail "engine was never resolved" }
    if ($text -match "Backend READY") { Pass "backend reported ready" } else { Fail "backend never became ready" }
    if ($text -match "Navigating WebView2 to app") { Pass "navigated to the DSH UI" } else { Fail "never navigated to the UI" }
    if ($text -match "splash-faded") { Pass "splash handshake acked" } else { Warn "splash ack missing (used fallback timer)" }
    if ($text -match "token=[^*\s]") { Fail "log contains an unmasked token" } else { Pass "no unmasked token in log" }
    if ($text -match "Rejected ready URL") { Fail "the engine printed a URL the shell refused to navigate to" } else { Pass "ready URL passed validation" }
    if ($text -match "WebView2 init failed") { Warn "at least one WebView2 user-data candidate failed (see the log)" } else { Pass "WebView2 opened on the first candidate" }
    if ($loaded) { Pass "app page loaded within ${TimeoutSeconds}s" } else { Fail "app page did not load in time" }
    if ($stillRunning) { Fail "the launched shell is still running after close" } else { Pass "launched process exited cleanly on close" }

    # The isolated run starts its own engine; once the shell is closed, nothing
    # beyond the pre-existing set may remain.
    $afterPids = Get-EnginePids
    $leftBehind = @($afterPids | Where-Object { $_ -notin $enginesBefore })
    if ($leftBehind.Count -eq 0) { Pass "no orphaned engine process" }
    else { Fail "$($leftBehind.Count) engine process(es) left behind by the isolated run (pid $($leftBehind -join ', '))" }

    Write-Host ""
    Write-Host "SMOKE TEST PASSED" -ForegroundColor Green
}
finally {
    Remove-Item Env:\DSHSHELL_LOG, Env:\DSHSHELL_INSTANCE, Env:\DSHSHELL_WEBVIEW2_DIR, Env:\DSH_HOME -ErrorAction SilentlyContinue
    # Keep the log on failure: it is the only evidence of what went wrong.
    if ($loaded) { Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "  (run directory kept for inspection: $runDir)" -ForegroundColor Yellow }
}
