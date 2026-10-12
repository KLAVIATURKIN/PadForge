# Themed-colorway + VR capture, by WRITING STATE rather than driving UI.
#
# Why this exists separately from capture_all.ps1: the appearance picker and
# the Devices-page assignment toggles are the two least reliable UI paths in
# the app for automation (the full harness stalls in the toggle-enumeration
# chain, which memory has recorded dying across four separate runs). Every
# input this script needs is a persisted field:
#
#   AppSettings.SlotModel3DAppearances  "DualSense=SpiderMan2,XboxSeries=Starfield"
#   AppSettings.Use2DControllerView  true|false
#   AppSettings.SlotCreated / SlotControllerTypes  the slot itself
#
# So it injects, launches, clicks only to NAVIGATE, captures, and restores.
# ASCII-only (PS 5.1 reads a BOM-less .ps1 as ANSI).

param(
    [string]$OutputDir   = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml",
    # The owner's answer to a run that stopped over a settings backup lying
    # beside the settings file (see "The owner's settings file" below). Ask,
    # the default, is that stop. File keeps the settings file and sets the
    # backup aside. Backup puts the backup back over the settings file. A run
    # with nothing to ask refuses File and Backup.
    [ValidateSet('Ask', 'File', 'Backup')]
    [string]$KeepSettingsFrom = 'Ask'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$log = Join-Path $pfLogDir "vr_out.txt"
function Note($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Add-Content $log $line -Encoding utf8
    Write-Host $line
}
Set-Content $log "colorway capture start" -Encoding utf8

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

# ---- Win32 interop: capture + real clicks (UIA cannot drive some of this) ----
Add-Type -Namespace Win32 -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, int e);
public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Shot($name) {
    $r = New-Object Win32.U+RECT
    [void][Win32.U]::GetWindowRect($script:hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Note "  !! bad rect for $name"; $script:missed += $name; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path $OutputDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Note "  >> $name.png ($([math]::Round((Get-Item $path).Length / 1KB))KB)"
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$TD = [System.Windows.Automation.TreeScope]::Descendants

function Find-ByName($name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $script:uiaWin.FindFirst($TD, $cond)
}

function Click-Rect($el, $label) {
    if (-not $el) { Note "  !! no element for $label"; return $false }
    $r = $el.Current.BoundingRectangle
    if ($r.IsEmpty) { Note "  !! empty rect for $label"; return $false }
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [void][Win32.U]::SetForegroundWindow($script:hwnd)
    [void][Win32.U]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [Win32.U]::mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    [Win32.U]::mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
    Note "  click '$label' at ($x,$y)"
    Start-Sleep -Milliseconds 900
    return $true
}

function Kill-PadForge {
    # taskkill writes "ERROR: The process ... not found." to STDERR when the
    # app is not running. Under $ErrorActionPreference='Stop' PowerShell 5.1
    # turns that into a terminating NativeCommandError, which killed this
    # script at its first line every run (the log stopped at "capture start"
    # and nothing else ever happened). Suppress locally rather than globally.
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    for ($k = 1; $k -le 6; $k++) {
        try { & taskkill.exe /F /IM PadForge.exe 2>&1 | Out-Null } catch { }
        Start-Sleep 3
        $live = @(Get-Process PadForge -ErrorAction SilentlyContinue).Count
        if ($live -eq 0) { break }
        Note "  kill attempt ${k}: $live instance(s) still up"
    }
    $ErrorActionPreference = $old
    # COUNT the processes, never trust the exit code. PadForge runs elevated,
    # so taskkill reports success having killed nothing when this script is
    # not, and every write after that lands under a live app that re-saves its
    # own state over it. Refuse to continue rather than clobber the settings.
    if (@(Get-Process PadForge -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'PadForge survived the kill; refusing to write or restore settings it would clobber.'
    }
}

# ---- The scenes. One slot per family, appearance written into the slot. ----
# Hero first: the Spider-Man 2 DualSense is the owner's chosen top shot.
$scenes = @(
    # VR (#49): type 6. No HIDMaestro catalog profile exists for the VR slot
    # (GetDefaultProfileId returns null for it), so Profile is empty and the
    # driver ships its one honest identity. The slot only appears when
    # SteamVR is installed, which is why the runtime was restored first.
    @{ Shot = "pad-vr-preview"; Type = 6; Profile = ""; Fam = "DualSense"; App = "White" }
)
# The shots this run skipped or could not save, for the exit code.
$script:missed = @()

# ---- Backup with the clobber guard (never overwrite an existing backup) ----
$bak = "$PadForgeXml.bak"
# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. A backup a killed run left
# holds the owner's real settings, and the file beside it is capture residue.
# One left behind after the settings were put back by hand sits beside the
# owner's current file, and restoring it would take every setting changed
# since. The marker tells the two apart (Get-BackupDoubt). Without its word
# the run stops and the owner decides. -KeepSettingsFrom answers that stop and
# nothing else, so a run with nothing to ask refuses it.
$doubt = Get-BackupDoubt $PadForgeXml $bak 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $PadForgeXml $bak $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { Add-Content $log $line -Encoding utf8; Write-Host $line -ForegroundColor Red }
    exit 1
}
# STOP THE APP FIRST. The restore used to run ahead of the kill and then delete
# the backup, so a live PadForge could save its own state over the owner's
# settings with no copy left to recover from.
Kill-PadForge
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ, and the backup is then
# no longer known to hold the settings. The run stops with neither file
# touched and starts PadForge again.
$doubt = Get-BackupDoubt $PadForgeXml $bak $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $PadForgeXml $bak $doubt)) { Add-Content $log $line -Encoding utf8; Write-Host $line -ForegroundColor Red }
    Start-Process $PadForgeExe
    exit 1
}
if (Test-Path $bak) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $bak
        Remove-Item $bak -Force
        Remove-BackupMarker $bak
        Note "-KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept"
    } else {
        $kept = Save-SettingsCopy $PadForgeXml
        Note "leftover backup found: restoring it BEFORE the capture"
        if ($kept) { Note "the file it replaces is kept as $kept" }
        Copy-Item $bak $PadForgeXml -Force
    }
}
# A backup is written only where none is, so a copy cut short can never leave
# half a file over a good backup. One just put back already holds what the
# settings file holds.
if (-not (Test-Path $bak)) { Copy-Item $PadForgeXml $bak -Force }
Set-BackupMarker $bak
Note "backed up owner settings ($((Get-Item $bak).Length) bytes)"

try {
    foreach ($sc in $scenes) {
        # --- write the scene into a fresh copy of the owner's settings ---
        $xml = Read-SettingsXml $bak
        $ns = $xml.DocumentElement
        # THE TRAP that cost three runs of wrong images: the slot arrays and
        # the app flags do NOT live at the document root. The real path is
        # PadForgeSettings > AppSettings, and PadSettings is the root child.
        # Writing them at the root created ignored duplicate elements while
        # the real values sat untouched, so every scene rendered whatever the
        # slot already was (a Switch Pro) no matter what was "written".
        $app = $ns.SelectSingleNode("AppSettings")
        if (-not $app) { throw "AppSettings node missing from PadForge.xml" }

        function Set-Node($parent, $name, $value) {
            $n = $parent.SelectSingleNode($name)
            if (-not $n) { $n = $xml.CreateElement($name); [void]$parent.AppendChild($n) }
            $n.InnerText = $value
        }

        # The scene is stored in the default profile fields.
        Set-Node $app "ActiveProfileId" ""
        Set-Node $app "EnableAutoProfileSwitching" "false"

        # One slot, created and enabled, of the scene's type. The arrays are
        # element-per-item; rebuild them wholesale so there is no stale tail.
        foreach ($pair in @(@("SlotCreated","Created"), @("SlotEnabled","Enabled"), @("SlotControllerTypes","Type"))) {
            $arr = $app.SelectSingleNode($pair[0])
            if (-not $arr) { $arr = $xml.CreateElement($pair[0]); [void]$app.AppendChild($arr) }
            while ($arr.HasChildNodes) { [void]$arr.RemoveChild($arr.FirstChild) }
            for ($i = 0; $i -lt 16; $i++) {
                $e = $xml.CreateElement($pair[1])
                $e.InnerText = switch ($pair[0]) {
                    "SlotCreated"         { if ($i -eq 0) { "true" } else { "false" } }
                    "SlotEnabled"         { if ($i -eq 0) { "true" } else { "false" } }
                    "SlotControllerTypes" { if ($i -eq 0) { "$($sc.Type)" } else { "0" } }
                }
                [void]$arr.AppendChild($e)
            }
        }

        # The slot TYPE alone does not choose the model: SlotProfileIds picks
        # the HIDMaestro profile, and that is what the preview renders. The
        # first run wrote type=PlayStation onto a slot whose stored profile
        # was still "switch-pro" and captured a Switch Pro in every shot.
        # InputManager.GetDefaultProfileId: Xbox -> xbox-series-xs-bt,
        # PlayStation -> dualsense-composite.
        $profArr = $app.SelectSingleNode("SlotProfileIds")
        if (-not $profArr) { $profArr = $xml.CreateElement("SlotProfileIds"); [void]$app.AppendChild($profArr) }
        while ($profArr.HasChildNodes) { [void]$profArr.RemoveChild($profArr.FirstChild) }
        for ($i = 0; $i -lt 16; $i++) {
            $e = $xml.CreateElement("Id")
            if ($i -eq 0) { $e.InnerText = $sc.Profile }
            else { [void]$e.SetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance", "true") }
            [void]$profArr.AppendChild($e)
        }

        # 3D view (the colorways are the point), tour off, English.
        Set-Node $app "Use2DControllerView" "false"
        Set-Node $app "FirstRunTourCompleted" "true"
        Set-Node $app "StartMinimized" "false"
        Set-Node $app "Language" "en"

        # Slot 0's appearance is independent of assigned input devices.
        $appearanceArr = $app.SelectSingleNode("SlotModel3DAppearances")
        if (-not $appearanceArr) {
            $appearanceArr = $xml.CreateElement("SlotModel3DAppearances")
            [void]$app.AppendChild($appearanceArr)
        }
        while ($appearanceArr.HasChildNodes) { [void]$appearanceArr.RemoveChild($appearanceArr.FirstChild) }
        for ($i = 0; $i -lt 16; $i++) {
            $e = $xml.CreateElement("Appearance")
            $e.InnerText = if ($i -eq 0) { "$($sc.Fam)=$($sc.App)" } else { "" }
            [void]$appearanceArr.AppendChild($e)
        }

        $kept = Save-SettingsXml $xml $PadForgeXml
        Note "scene $($sc.Shot): type=$($sc.Type) $($sc.Fam)=$($sc.App) (the file before it is kept as $kept)"

        # --- launch, navigate, capture ---
        Start-Process $PadForgeExe
        Start-Sleep 14
        $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if (-not $proc) { Note "  !! PadForge did not start"; $script:missed += $sc.Shot; continue }
        $script:hwnd = $proc.MainWindowHandle
        [void][Win32.U]::ShowWindow($script:hwnd, 3)   # SW_MAXIMIZE
        [void][Win32.U]::SetForegroundWindow($script:hwnd)
        Start-Sleep 2
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)

        # Navigate via the DASHBOARD card, never the sidebar entry. The
        # sidebar slot card carries a row of type-switch tiles (Xbox /
        # PlayStation / Nintendo / Extended / KBM / MIDI / VR), and a click
        # at its center lands ON that row: the first cut clicked (159,333),
        # hit the Nintendo tile, and every "DualSense" scene captured a
        # Switch Pro because the click had CHANGED the slot's type before
        # the shot. capture_all.ps1 carries the same rule for the same
        # reason ("Dashboard SlotsItemsControl cards, NOT the sidebar").
        # RETRY the lookup, and never shoot a scene that failed to stage, as
        # capture_colorways.ps1 learned: a frame taken when the card was never
        # found or clicked shows the dashboard under the preview's name. A miss
        # is a skipped shot, which keeps the previous file and ends the run
        # with exit code 1.
        $aidCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")
        $card = $null
        for ($t = 1; $t -le 4; $t++) {
            $slotsHost = $script:uiaWin.FindFirst($TD, $aidCond)
            if ($slotsHost) {
                $kids = $slotsHost.FindAll([System.Windows.Automation.TreeScope]::Children,
                            [System.Windows.Automation.Condition]::TrueCondition)
                if ($kids.Count -gt 0) { $card = $kids[0]; break }
            }
            Note "  dashboard slot card not up yet (try $t)"
            Start-Sleep 4
        }
        if (-not $card) {
            Note "  !! SKIPPED $($sc.Shot): dashboard never realized the slot card"
            $script:missed += $sc.Shot
            Kill-PadForge
            Start-Sleep 3
            continue
        }
        if (-not (Click-Rect $card "dashboard slot card 1")) {
            Note "  !! SKIPPED $($sc.Shot): the slot card could not be clicked"
            $script:missed += $sc.Shot
            Kill-PadForge
            Start-Sleep 3
            continue
        }
        Start-Sleep 2
        Shot $sc.Shot

        Kill-PadForge
        Start-Sleep 3
    }
}
finally {
    Kill-PadForge
    Start-Sleep 2
    # The file about to be replaced holds nothing but what the backup holds
    # and what this run wrote, so its copy is a courtesy and a failed one does
    # not hold the restore up. The marker goes as soon as the settings are
    # back, before the backup does: a run that dies between the two leaves a
    # backup with no marker beside a file with the same bytes, which the next
    # run takes for what it is. The other order would leave a marker vouching
    # for a backup the owner has since moved on from.
    $kept = $null
    $keptBak = $null
    try { $kept = Save-SettingsCopy $PadForgeXml }
    catch { Note "  !! could not copy the capture file aside before the restore: $($_.Exception.Message)" }
    Copy-Item $bak $PadForgeXml -Force
    Remove-BackupMarker $bak
    try {
        $keptBak = Save-SettingsCopy $bak
        Remove-Item $bak -Force
    } catch { Note "  !! the backup could not be cleared and stays beside the restored file: $($_.Exception.Message)" }
    Note "RESTORED owner settings"
    if ($kept) { Note "the file the restore replaced is kept as $kept" }
    if ($keptBak) { Note "the backup is kept as $keptBak" }
    Start-Process $PadForgeExe
    Note "relaunched PadForge"
}
# A scene skipped, or a shot not saved, keeps the old file under its name, so
# the run ends with exit code 1 and names them. The exit code is always
# written out: a script that simply ends hands its caller the exit code of
# its last native command.
if ($script:missed.Count -gt 0) {
    Note "=== DONE with $($script:missed.Count) SKIPPED: $($script:missed -join ', ') ==="
    exit 1
}
Note "=== DONE ==="
exit 0
