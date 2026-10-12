<#
.SYNOPSIS
    Recaptures the two Web Controller screenshots against a PROVEN-LIVE server.
.DESCRIPTION
    Split out of capture_all.ps1 because the web shots do not depend on the
    dummy-device topology the full run builds, and because they were the two
    that shipped broken on 2026-08-09: both were Edge's "localhost refused to
    connect" page, captured because the old code photographed whatever Edge
    rendered without ever asking whether the server had answered.

    This script refuses to capture until an HTTP 200 comes back, and refuses
    to keep a capture whose window title says otherwise.

    It no longer REQUIRES the server to be on already. The owner ships with
    EnableWebController=false, so "PadForge must already be running with the
    Web Controller enabled" meant every unattended run captured nothing. An
    assignment is data: this backs up PadForge.xml, writes the flag, restarts
    the app, captures, and restores the owner's file in a finally.

    The shot list covers the #296 rebuild rather than one legacy layout:
    the landing grid, the DualSense (lightbar, player LEDs and the analog
    trigger sliders), a Steam Deck (trackpads), a Switch 2 Pro, and the
    custom controller builder.
#>

param(
    [string]$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [int]$Port = 8080,
    # The owner's answer to a run that stopped over a settings backup an
    # earlier run left (settings_guard.ps1). Ask, the default, is that
    # stop. File keeps the settings file and sets the backup aside. Backup puts
    # the backup back over the settings file. A run with nothing to ask
    # refuses File and Backup.
    [ValidateSet('Ask', 'File', 'Backup')]
    [string]$KeepSettingsFrom = 'Ask'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[W32]::SetProcessDPIAware() | Out-Null

# #296 phase 0 binds a SELF-SIGNED cert and serves https:// whenever the
# binding succeeds, which it does whenever PadForge is elevated. Probing
# http:// therefore found nothing and every shot was skipped as "server
# down" while the server was up and answering on the secure lane. Trust
# the capture-local cert for probes, and tell Edge to do the same so it
# renders the controller instead of an interstitial.
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$edgeTempProfile = Join-Path $env:TEMP "PadForge_EdgeCapture"

function Kill-CaptureEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
}

# The gate. No 200, no capture, no exceptions.
#
# curl.exe, not Invoke-WebRequest. PowerShell 5.1's client cannot complete
# the handshake against the #296 self-signed cert: schannel renegotiates
# and Invoke-WebRequest returns "the underlying connection was closed" for
# BOTH schemes. That reads exactly like a dead server, and it is why six
# shots were skipped as "server down" while curl -k got HTTP 200 and
# 11330 bytes from the same URL a second later. curl.exe ships in
# System32 on every supported Windows.
function Probe-Web {
    param([string]$Url)
    $code = & curl.exe -sk -o NUL -w '%{http_code}' --max-time 6 $Url 2>$null
    return ($code -eq '200')
}

function Wait-Web {
    param([string]$Url, [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Probe-Web $Url) {
            Write-Host "  answering: $Url" -ForegroundColor Green
            return $true
        }
        Start-Sleep -Milliseconds 800
    }
    Write-Host "  !! never answered: $Url" -ForegroundColor Red
    return $false
}

function Cap-Web {
    param([string]$Url, [string]$Name, [int]$WaitMs = 6000, [int]$Width = 1280, [int]$Height = 720)

    if (-not (Wait-Web $Url)) { Write-Host "  SKIP $Name (server down)" -ForegroundColor Yellow; return $false }

    Kill-CaptureEdge
    Start-Sleep -Milliseconds 1200

    Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --ignore-certificate-errors --allow-insecure-localhost --test-type --app=$Url"
    Start-Sleep -Milliseconds $WaitMs

    $ehwnd = [IntPtr]::Zero
    $title = ""
    $pids = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($procId in $pids) {
        $ep = Get-Process -Id $procId -EA SilentlyContinue
        if ($ep -and $ep.MainWindowHandle -ne [IntPtr]::Zero) {
            $ehwnd = $ep.MainWindowHandle; $title = $ep.MainWindowTitle
            Write-Host "  Edge window PID=$procId title='$title'"
            break
        }
    }
    if ($ehwnd -eq [IntPtr]::Zero) {
        Write-Host "  !! no temp-profile Edge window -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    # A title that still says the host name means Edge is on its own error page,
    # because the served pages set a real <title>. Refuse rather than ship it.
    if ($title -match '^\s*localhost' -or $title -match "can't be reached" -or $title -match 'refused') {
        Write-Host "  !! Edge is showing an error page (title='$title') -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    [W32]::SetWindowPos($ehwnd, [IntPtr]::Zero, 120, 60, $Width, $Height, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 400
    [W32]::SetForegroundWindow($ehwnd) | Out-Null
    Start-Sleep -Milliseconds 700

    # GetWindowRect returns the rect INCLUDING the invisible resize border,
    # so a capture taken from it carries ~11px of whatever sits behind the
    # window down its left edge and ~8px down its right. On a dark page that
    # reads as a second window bleeding out of the first. DWM's extended
    # frame bounds is the visible frame, which is what a screenshot means.
    $r = New-Object W32+RECT
    $DWMWA_EXTENDED_FRAME_BOUNDS = 9
    $sz = [System.Runtime.InteropServices.Marshal]::SizeOf([type][W32+RECT])
    if ([W32]::DwmGetWindowAttribute($ehwnd, $DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$r, $sz) -ne 0) {
        [W32]::GetWindowRect($ehwnd, [ref]$r) | Out-Null
    }
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 100 -or $h -le 100) {
        Write-Host "  !! window too small ${w}x${h} -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $out = Join-Path $OutputDir "$Name.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  >> $Name.png ($([math]::Round((Get-Item $out).Length/1024))KB)" -ForegroundColor Green

    Kill-CaptureEdge
    Start-Sleep -Milliseconds 500
    return $true
}

Write-Host "Web Controller capture, port $Port" -ForegroundColor Cyan

$exe   = 'C:\PadForge\PadForge.exe'
$pfXml = 'C:\PadForge\PadForge.xml'
$pfBak = Join-Path $env:TEMP 'PadForge.xml.webcapture.bak'

# VERIFY the kill before writing. A surviving instance re-saves PadForge.xml
# from its own in-memory state on exit and silently puts EnableWebController
# back to false, so the write lands, the app starts, and the server never
# binds. That is exactly how this ran three times reporting "server down"
# while the setting on disk read true. taskkill also returns success having
# killed nothing when the caller is not elevated and PadForge is, so count the
# processes rather than trusting the exit code. taskkill writes to stderr when
# no PadForge runs, and under ErrorActionPreference Stop Windows PowerShell 5.1
# turns that into a terminating error even through 2>$null, so it runs with
# Continue here. A PadForge that survives stops the run: no settings file is
# written or put back under it.
function Kill-PadForge {
    $ErrorActionPreference = 'Continue'
    for ($k = 1; $k -le 6; $k++) {
        taskkill /F /IM PadForge.exe 2>$null | Out-Null
        Start-Sleep 3
        $live = @(Get-Process PadForge -ErrorAction SilentlyContinue).Count
        if ($live -eq 0) { break }
        Write-Host "  kill attempt ${k}: $live instance(s) still up" -ForegroundColor Yellow
    }
    if (@(Get-Process PadForge -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'PadForge instances survived the kill; refusing to write settings that would be clobbered.'
    }
}

# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. A backup a killed run left
# holds the owner's real settings, and the file beside it is capture residue.
# One left behind after the settings were put back by hand sits beside the
# owner's current file. The marker tells the two apart (Get-BackupDoubt), and
# without its word the run stops and the owner decides.
$doubt = Get-BackupDoubt $pfXml $pfBak 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $pfXml $pfBak $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { Write-Host $line -ForegroundColor Red }
    exit 1
}
# STOP THE APP FIRST. A leftover backup put back under a live PadForge would
# be saved over by the app's own state.
Kill-PadForge
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ. The run stops with
# neither file touched and starts PadForge again.
$doubt = Get-BackupDoubt $pfXml $pfBak $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $pfXml $pfBak $doubt)) { Write-Host $line -ForegroundColor Red }
    Start-Process -FilePath $exe
    exit 1
}
if (Test-Path $pfBak) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $pfBak
        Remove-Item $pfBak -Force
        Remove-BackupMarker $pfBak
        Write-Host "  -KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept" -ForegroundColor Yellow
    } else {
        $kept = Save-SettingsCopy $pfXml
        Write-Host "  restoring the backup an earlier run left" -ForegroundColor Yellow
        if ($kept) { Write-Host "  the file it replaces is kept as $kept" -ForegroundColor Yellow }
        Copy-Item $pfBak $pfXml -Force
    }
}
# A backup is written only where none is, so a copy cut short can never leave
# half a file over a good backup. With no settings file there is nothing to
# back up, and no marker either.
if (-not (Test-Path $pfBak) -and (Test-Path $pfXml)) { Copy-Item $pfXml $pfBak -Force }
Set-BackupMarker $pfBak

$results = @{}
try {
    # Turn the server on in the file, not through the UI.
    $x = Read-SettingsXml $pfXml
    foreach ($n in @($x.SelectNodes("//EnableWebController"))) { if ($n.InnerText -eq 'false') { $n.InnerText = 'true' } }
    $kept = Save-SettingsXml $x $pfXml
    Write-Host "  EnableWebController=true written; restarting PadForge. The file before it is kept as $kept" -ForegroundColor Cyan
    Start-Process -FilePath $exe
    Start-Sleep 18

    # Discover the HOST as well as the scheme. The server binds the
    # machine's LAN address, not the loopback: its whole point is a phone on
    # the same network, and the Dashboard prints that address beside its QR
    # code ("Running on https://10.19.90.40:8080"). Probing localhost
    # answers nothing on either scheme, which reads exactly like a dead
    # server and skipped all six shots in the 4.4.0 run. Try loopback first,
    # then every IPv4 address this machine holds.
    $hosts = @('localhost')
    try {
        $hosts += @(Get-NetIPAddress -AddressFamily IPv4 -EA SilentlyContinue |
            Where-Object { $_.IPAddress -ne '127.0.0.1' } |
            Select-Object -ExpandProperty IPAddress)
    } catch {}
    $base = $null
    foreach ($h in $hosts) {
        foreach ($try in 'https', 'http') {
            if (Probe-Web "${try}://${h}:${Port}/") { $base = "${try}://${h}:${Port}"; break }
        }
        if ($base) { break }
    }
    if (-not $base) { $base = "https://localhost:${Port}" }
    Write-Host "  server base: $base" -ForegroundColor Cyan

    $shots = @(
        @{ Url = "$base/";                                 Name = 'web-landing';    Wait = 6000; W = 1900; H = 1300 },
        @{ Url = "$base/controller.html?layout=xbox360";   Name = 'web-controller'; Wait = 9500 },
        @{ Url = "$base/controller.html?layout=dualsense"; Name = 'web-dualsense';  Wait = 9500 },
        @{ Url = "$base/controller.html?layout=steamdeck"; Name = 'web-steamdeck';  Wait = 9500 },
        @{ Url = "$base/controller.html?layout=switch2pro";Name = 'web-switch2pro'; Wait = 9500 },
        @{ Url = "$base/custom.html";                      Name = 'web-custom';     Wait = 7000 }
    )
    foreach ($s in $shots) {
        $h = if ($s.ContainsKey('H')) { $s.H } else { 720 }
        $w = if ($s.ContainsKey('W')) { $s.W } else { 1280 }
        $results[$s.Name] = Cap-Web $s.Url $s.Name $s.Wait $w $h
    }
}
finally {
    # Always put the owner's settings back, including after a mid-run error.
    # A PadForge that survives the kill stops the restore here: the backup and
    # its marker stay, and the next run puts the backup back.
    Kill-CaptureEdge
    if (Test-Path $pfBak) {
        Kill-PadForge
        # The file about to be replaced holds nothing but what the backup holds
        # and what this run wrote, so its copy is a courtesy and a failed one
        # does not hold the restore up. The marker goes as soon as the settings
        # are back, before the backup does.
        $kept = $null
        $keptBak = $null
        try { $kept = Save-SettingsCopy $pfXml }
        catch { Write-Host "  !! could not copy the capture file aside before the restore: $($_.Exception.Message)" -ForegroundColor Red }
        Copy-Item $pfBak $pfXml -Force
        Remove-BackupMarker $pfBak
        try {
            $keptBak = Save-SettingsCopy $pfBak
            Remove-Item $pfBak -Force
        } catch { Write-Host "  !! the backup could not be cleared and stays beside the restored file: $($_.Exception.Message)" -ForegroundColor Red }
        Write-Host "  owner settings restored" -ForegroundColor Cyan
        if ($kept) { Write-Host "  the file the restore replaced is kept as $kept" }
        if ($keptBak) { Write-Host "  the backup is kept as $keptBak" }
        Start-Process -FilePath $exe
    }
}

Write-Host ""
$failed = @($results.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
foreach ($k in $results.Keys | Sort-Object) { Write-Host ("  {0,-16} {1}" -f $k, $(if ($results[$k]) { 'ok' } else { 'FAILED' })) }
if ($failed.Count) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "all web shots captured" -ForegroundColor Green
