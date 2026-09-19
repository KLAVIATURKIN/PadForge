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
    # Shot names to capture, for re-taking the one or two a run got wrong
    # without spending twenty minutes on the eighteen it got right. Empty
    # means every scene. Names match the Shot field below.
    [string[]]$Only      = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$log = Join-Path $pfLogDir "colorway_out.txt"
function Note($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Add-Content $log $line -Encoding utf8
    Write-Host $line
}
Set-Content $log "colorway capture start" -Encoding utf8

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
    if ($w -le 0 -or $h -le 0) { Note "  !! bad rect for $name"; return }
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
    @{ Shot = "colorway-dualsense-spiderman2";  Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "SpiderMan2" },
    @{ Shot = "colorway-dualsense-ffxvi";       Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "FFXVI" },
    @{ Shot = "colorway-dualsense-cosmicred";   Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "CosmicRed" },
    @{ Shot = "colorway-dualsense-novapink";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "NovaPink" },
    @{ Shot = "colorway-dualsense-cobalt";      Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "DeepEarthCobalt" },
    @{ Shot = "colorway-dualsense-volcanic";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "DeepEarthVolcanic" },
    @{ Shot = "colorway-dualsense-graycamo";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "GrayCamo" },
    @{ Shot = "colorway-dualsense-sterling";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "DeepEarthSterling" },
    @{ Shot = "colorway-xbox-halo";             Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "HaloInfinite" },
    @{ Shot = "colorway-xbox-starfield";        Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Starfield" },
    @{ Shot = "colorway-xbox-stellarshift";     Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "StellarShift" },
    @{ Shot = "colorway-xbox-porsche";          Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Porsche75th" },
    @{ Shot = "colorway-xbox-velocitygreen";    Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "VelocityGreen" },
    @{ Shot = "colorway-xbox-remix";            Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Remix" },
    @{ Shot = "colorway-xbox-daystrike";        Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "DaystrikeCamo" },
    # The eight skins this cycle added. Sonic leads the gallery, so it is
    # captured first and the rest follow in the order the picker lists them.
    @{ Shot = "colorway-xbox-sonic";            Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Sonic" },
    @{ Shot = "colorway-xbox-razer";            Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Razer" },
    @{ Shot = "colorway-xbox-captainamerica";   Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "CaptainAmerica" },
    @{ Shot = "colorway-xbox-bobafett";         Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "BobaFett" },
    @{ Shot = "colorway-xbox-mandalorian";      Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Mandalorian" },
    @{ Shot = "colorway-xbox-stormtrooper";     Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Stormtrooper" },
    @{ Shot = "colorway-xbox-darthvader";       Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "DarthVader" },
    @{ Shot = "colorway-xbox-squadrons";        Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Squadrons" }
)

if ($Only.Count -gt 0) {
    # Accept both -Only a,b (one comma-joined string, which is what an
    # elevated Start-Process -File hands over) and -Only a b.
    $wanted = @($Only | ForEach-Object { $_ -split ',' } |
                ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $scenes = @($scenes | Where-Object { $wanted -contains $_.Shot })
    if ($scenes.Count -ne $wanted.Count) {
        throw "asked for $($wanted.Count) scene(s), matched $($scenes.Count). Check the names against the Shot fields."
    }
    Note "filtered to $($scenes.Count) scene(s): $($Only -join ', ')"
}
$script:missed = @()

# ---- Backup with the clobber guard (never overwrite an existing backup) ----
$bak = "$PadForgeXml.bak"
# STOP THE APP FIRST. The restore used to run ahead of the kill and then delete
# the backup, so a live PadForge could save its own state over the owner's
# settings with no copy left to recover from. The backup is the owner's real
# settings and the current file is capture residue.
Kill-PadForge
if (Test-Path $bak) {
    Note "leftover backup found: restoring it BEFORE taking a new one"
    Copy-Item $bak $PadForgeXml -Force
    Remove-Item $bak -Force
}
Copy-Item $PadForgeXml $bak -Force
Note "backed up owner settings ($((Get-Item $bak).Length) bytes)"

try {
    foreach ($sc in $scenes) {
        # --- write the scene into a fresh copy of the owner's settings ---
        [xml]$xml = Get-Content $bak -Raw
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

        $xml.Save($PadForgeXml)
        Note "scene $($sc.Shot): type=$($sc.Type) $($sc.Fam)=$($sc.App)"

        # --- launch, navigate, capture ---
        Start-Process $PadForgeExe
        Start-Sleep 14
        $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if (-not $proc) { Note "  !! PadForge did not start"; continue }
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
        # RETRY the lookup, and never shoot a scene that failed to stage. A
        # single try at a fixed 14s wait missed the card on two scenes of a
        # 21-scene run and captured both anyway: the appearance popup was
        # still open across the controller, and those two files would have
        # mirrored to the site looking like a UI bug. A miss is a skipped
        # shot, which leaves the previous good file in place and shows up in
        # the log, not a picture nobody can trust.
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
        [void](Click-Rect $card "dashboard slot card 1")
        Start-Sleep 2
        Shot $sc.Shot

        Kill-PadForge
        Start-Sleep 3
    }
}
finally {
    Kill-PadForge
    Start-Sleep 2
    Copy-Item $bak $PadForgeXml -Force
    Remove-Item $bak -Force
    Note "RESTORED owner settings"
    Start-Process $PadForgeExe
    Note "relaunched PadForge"
}
if ($script:missed.Count -gt 0) {
    Note "=== DONE with $($script:missed.Count) SKIPPED: $($script:missed -join ', ') ==="
} else {
    Note "=== DONE ==="
}
