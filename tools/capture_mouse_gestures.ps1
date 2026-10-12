<#
.SYNOPSIS
    Captures pad-mouse-gestures on its own, with the full slot topology.
.DESCRIPTION
    The full run takes about twenty five minutes, and re-running it to recover a
    single shot is not a reasonable way to fix one picture. This does the same
    thing for one shot in about two minutes.

    It builds the same seven-slot topology the rest of the gallery shows, so the
    sidebar matches every other screenshot, maps "All Mice (Merged)" onto the
    Keyboard and Mouse slot by writing UserSetting.MapTo (the pad page's device
    dropdown never picked it up from a UI toggle), opens the Mouse tab and
    captures it.

    Pad index is resolved from SlotControllerTypes, not assumed. Dashboard card
    order is type-group order while pad index is creation order, and mixing them
    is what put the mouse on an Extended slot.

    The owner's settings are backed up first and restored in a finally, under
    the rules of settings_guard.ps1: a backup an earlier run left goes back
    only on its marker's word, and every settings file this overwrites or
    deletes is copied to %TEMP%\PadForge_Capture first.
#>

param(
    [string]$OutputDir   = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml",
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

$mgLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $mgLogDir)) { New-Item -ItemType Directory -Path $mgLogDir | Out-Null }
Start-Transcript -Path (Join-Path $mgLogDir "mousegestures.txt") -Force | Out-Null

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MG {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[MG]::SetProcessDPIAware() | Out-Null

$TD = [System.Windows.Automation.TreeScope]::Descendants
$bak = Join-Path $env:TEMP "PadForge.xml.mousegestures.bak"

function Get-Rect($el) {
    if (-not $el) { return $null }
    try { $r = $el.Current.BoundingRectangle } catch { return $null }
    if ($null -eq $r -or $r -isnot [System.Windows.Rect]) { return $null }
    try { if ($r.IsEmpty) { return $null } } catch { return $null }
    if ($r.Width -le 0 -or $r.Height -le 0) { return $null }
    return $r
}

# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. A backup a killed run left
# holds the owner's real settings, and the file beside it is capture residue.
# One left behind after the settings were put back by hand sits beside the
# owner's current file. The marker tells the two apart (Get-BackupDoubt), and
# without its word the run stops and the owner decides.
$doubt = Get-BackupDoubt $PadForgeXml $bak 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $PadForgeXml $bak $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { Write-Host $line -ForegroundColor Red }
    Stop-Transcript | Out-Null
    exit 1
}
# STOP THE APP FIRST. A leftover backup put back under a live PadForge would
# be saved over by the app's own state.
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ. The run stops with
# neither file touched and starts PadForge again.
$doubt = Get-BackupDoubt $PadForgeXml $bak $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $PadForgeXml $bak $doubt)) { Write-Host $line -ForegroundColor Red }
    Start-Process $PadForgeExe
    Stop-Transcript | Out-Null
    exit 1
}
if (Test-Path $bak) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $bak
        Remove-Item $bak -Force
        Remove-BackupMarker $bak
        Write-Host "-KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept"
    } else {
        $kept = Save-SettingsCopy $PadForgeXml
        Write-Host "restoring the backup an earlier run left"
        if ($kept) { Write-Host "the file it replaces is kept as $kept" }
        Copy-Item $bak $PadForgeXml -Force
    }
}
# A backup is written only where none is, so a copy cut short can never leave
# half a file over a good backup.
if (-not (Test-Path $bak)) { Copy-Item $PadForgeXml $bak -Force }
Set-BackupMarker $bak

# Set by the catch below, for the exit code.
$script:failed = $false
try {
    # Seven slots, matching the rest of the gallery.
    # 0 Xbox, 1 PlayStation, 5 Nintendo, 4 KeyboardMouse, 2 Extended, 3 MIDI, 6 VR
    $wanted = @(0, 1, 5, 4, 2, 3, 6)
    $x = Read-SettingsXml $PadForgeXml
    $root = $x.PadForgeSettings

    # The slot arrays live under AppSettings, NOT at the root. Project memory
    # says so plainly ("slot arrays and app flags under PadForgeSettings >
    # AppSettings") and I still looked for them at the root first.
    $app = $root.SelectSingleNode("AppSettings")
    if (-not $app) { throw "AppSettings node missing" }
    $created = $app.SelectSingleNode("SlotCreated")
    $types   = $app.SelectSingleNode("SlotControllerTypes")
    $enabled = $app.SelectSingleNode("SlotEnabled")
    if (-not $created) { throw "SlotCreated node missing" }
    if (-not $types)   { throw "SlotControllerTypes node missing" }
    $nCreated = @($created.ChildNodes).Count
    $nTypes   = @($types.ChildNodes).Count
    $nEnabled = if ($enabled) { @($enabled.ChildNodes).Count } else { 0 }
    Write-Host "slot arrays: created=$nCreated types=$nTypes enabled=$nEnabled"
    for ($i = 0; $i -lt $nCreated; $i++) {
        $on = ($i -lt $wanted.Count)
        $created.ChildNodes[$i].InnerText = if ($on) { "true" } else { "false" }
        if ($i -lt $nEnabled) { $enabled.ChildNodes[$i].InnerText = if ($on) { "true" } else { "false" } }
        if ($on -and $i -lt $nTypes) { $types.ChildNodes[$i].InnerText = "$($wanted[$i])" }
    }
    $kbmPad = [Array]::IndexOf($wanted, 4)
    Write-Host "KBM slot is pad index $kbmPad"

    # Map the merged mouse onto that pad. It is already in the device cache.
    $dev = $null
    foreach ($d in $root.SelectSingleNode("Devices").ChildNodes) {
        $n = $d.SelectSingleNode("InstanceName")
        if ($n -and $n.InnerText -eq "All Mice (Merged)") { $dev = $d; break }
    }
    if (-not $dev) { throw "All Mice (Merged) is not in the device cache" }
    $guid  = $dev.SelectSingleNode("InstanceGuid").InnerText
    $pguid = $dev.SelectSingleNode("ProductGuid").InnerText

    $us = $root.SelectSingleNode("UserSettings")
    $template = $us.FirstChild
    $row = $template.CloneNode($true)
    foreach ($pair in @(@("InstanceGuid",$guid), @("ProductGuid",$pguid),
                        @("InstanceName","All Mice (Merged)"), @("ProductName","All Mice (Merged)"),
                        @("MapTo","$kbmPad"))) {
        $n = $row.SelectSingleNode($pair[0]); if ($n) { $n.InnerText = $pair[1] }
    }
    $us.AppendChild($row) | Out-Null

    $ft = $root.SelectSingleNode("AppSettings/FirstRunTourCompleted")
    if ($ft) { $ft.InnerText = "true" }
    $kept = Save-SettingsXml $x $PadForgeXml
    Write-Host "wrote 7 slots and mapped the mouse to pad $kbmPad (the file before it is kept as $kept)"

    Start-Process $PadForgeExe
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $p = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "PadForge did not start" }
    Start-Sleep -Seconds 10
    [MG]::ShowWindow($hwnd, 3) | Out-Null      # maximize
    [MG]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds 2
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    function Click-Named([string]$name, [int]$delay = 900) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        foreach ($el in $win.FindAll($TD, $cond)) {
            $r = Get-Rect $el
            if ($null -eq $r) { continue }
            [MG]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 120
            [MG]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            Start-Sleep -Milliseconds $delay
            Write-Host "  clicked '$name'"
            return $true
        }
        Write-Host "  !! '$name' not found" -ForegroundColor Yellow
        return $false
    }

    # The KBM slot card sits fifth in the sidebar (type-group order).
    $slots = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")))
    if (-not $slots) { throw "SlotsItemsControl not found" }
    $cards = @($slots.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
    Write-Host "  slot cards: $($cards.Count)"
    $kbmCard = 4   # Xbox, PlayStation, Nintendo, Extended, KBM in card order
    $r = Get-Rect $cards[$kbmCard]
    if ($null -eq $r) { throw "KBM card has no rect" }
    [MG]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Seconds 2

    Click-Named "Mouse" 1200 | Out-Null
    Start-Sleep -Seconds 1

    $wr = New-Object MG+RECT
    [MG]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $out = Join-Path $OutputDir "pad-mouse-gestures.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ">> pad-mouse-gestures.png ($([math]::Round((Get-Item $out).Length/1024))KB)" -ForegroundColor Green
}
catch {
    $script:failed = $true
    Write-Host "!! FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   at $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" -ForegroundColor Red
}
finally {
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    # The file about to be replaced holds nothing but what the backup holds
    # and what this run wrote, so its copy is a courtesy and a failed one does
    # not hold the restore up. The marker goes as soon as the settings are
    # back, before the backup does.
    $kept = $null
    $keptBak = $null
    try { $kept = Save-SettingsCopy $PadForgeXml }
    catch { Write-Host "!! could not copy the capture file aside before the restore: $($_.Exception.Message)" -ForegroundColor Red }
    Copy-Item $bak $PadForgeXml -Force
    Remove-BackupMarker $bak
    try {
        $keptBak = Save-SettingsCopy $bak
        Remove-Item $bak -Force
    } catch { Write-Host "!! the backup could not be cleared and stays beside the restored file: $($_.Exception.Message)" -ForegroundColor Red }
    Start-Process $PadForgeExe
    Write-Host "owner settings restored" -ForegroundColor Green
    if ($kept) { Write-Host "the file the restore replaced is kept as $kept" }
    if ($keptBak) { Write-Host "the backup is kept as $keptBak" }
}
Stop-Transcript | Out-Null
# A failed run took no shot, and the catch above kept the error from ending
# the script, so the run ends with exit code 1 here. The exit code is always
# written out: a script that simply ends hands its caller the exit code of its
# last native command.
if ($script:failed) { exit 1 }
exit 0
