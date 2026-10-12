# Wrapper: runs capture_all.ps1 elevated and captures all output to a known log path.
# It ends with the capture's own exit code: 1 when a shot was refused, a state
# check failed, or the run stopped on an error or over a settings backup, and 0
# when it ran clean. It used to end with 0 whatever the capture did, a fatal
# error included.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$logFile = Join-Path $pfLogDir "capture_all_log.txt"
$code = 1
try {
    & "$scriptDir\capture_all.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii
    $code = $LASTEXITCODE
} catch {
    "FATAL ERROR: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
exit $code
