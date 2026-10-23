<#
.SYNOPSIS
  Runtime screen-reader audit of the running PadForge.

.DESCRIPTION
  Attaches to the PadForge window, selects each sidebar page by its
  AutomationId (Nav_Dashboard, Nav_Profiles, Nav_Devices, Nav_Pad<N>,
  Nav_Settings, Nav_About), selects each tab on a page that has tabs, opens
  each ComboBox once so its items exist, then walks the UI Automation control
  view and reports:
    FOCUSABLE_NO_NAME  a keyboard-focusable element with an empty name
    GLYPH_NAME         a name holding a private-use character, U+E000 to U+F8FF
    TYPE_NAME          a name shaped like a .NET type name (PadForge.ViewModels.X)
    INPUT_NO_NAME      an Edit, ComboBox or Slider without a name
    ITEM_NO_NAME       a Button, ListItem or DataItem without a name
  It covers the UI built in C# (the sidebar cards, the Add Controller entry),
  which the static XAML audit in AccessibilityXamlTests cannot see.

  One pass per verification point. The pass waits on conditions, never on
  fixed pauses: a click waits until UIA shows the new page or tab content, a
  combo box closes once its items exist, and each view is one cached UIA call
  walked in compiled C#. After the first view only the page area is read,
  since the sidebar does not change.

  Run it elevated. PadForge runs as administrator, and UI Automation does not
  reach into an elevated process from a lower one. Start PadForge first with
  the deploy recipe, since a PadForge started from inside an elevated script
  exits when that script's process tree ends. The window is brought forward
  only when another window is in front, restored only when minimized and
  never resized, because PadForge saves its size and state. Pages are
  selected by a click at the entry's center: the entry's UIA node is WPF's
  DataItem wrapper, which exposes no selection or invoke pattern
  (ItemAutomationPeer.GetPattern). The controller page's tabs are clicked too:
  they are RadioButtons whose Click handler switches the content, and a UIA
  Select checks one without raising Click. Nothing else is clicked, typed or
  selected, so no setting changes.

  Reports go to OutDir (default %LOCALAPPDATA%\PadForgeA11y), never beside
  PadForge.exe. Exit code: the number of findings, -1 when PadForge is not
  running, -2 on an error, which the report names with its line.

.EXAMPLE
  Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','tools\a11y_uia_audit.ps1','-Axe'
#>
param(
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'PadForgeA11y'),
    # Page tags to audit (Dashboard, Profiles, Devices, Pad1, Settings, About).
    # Default: every page in the sidebar.
    [string[]]$Pages,
    [switch]$NoTabs,
    [switch]$NoCombos,
    # Also scan each page and tab with the Axe.Windows CLI, built from
    # microsoft/axe-windows src/CLI. Its exit code is 0 for no errors, 1 for
    # errors and 2 for a failed scan (ReturnValueChooser.cs).
    [switch]$Axe,
    [string]$AxeCli = (Join-Path $env:LOCALAPPDATA 'PadForgeStudio\tooling\axe-cli\AxeWindowsCLI.exe')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$txt = Join-Path $OutDir "uia-audit-$stamp.txt"
$json = Join-Path $OutDir "uia-audit-$stamp.json"
$lines = New-Object System.Collections.Generic.List[string]
function Note([string]$m) { $lines.Add($m); Write-Host $m }
function Save { [System.IO.File]::WriteAllLines($txt, $lines, (New-Object System.Text.UTF8Encoding($true))) }
# The script runs in an elevated window that closes on exit, so an error goes
# into the report, with its line.
trap { Note "ERROR at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"; Save; exit -2 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class A11yWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
'@
# The rules and the walk run here rather than in a PowerShell loop over every
# element. C# 5, the language Add-Type compiles in Windows PowerShell 5.1.
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase @'
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;

public sealed class A11yFinding {
    public string Page; public string Rule; public string Type; public string Name; public string Aid;
    public string Class; public string Help; public bool Offscreen; public string Path; public string Rid;
}

public sealed class A11yView {
    public int Count;
    public List<A11yFinding> Findings = new List<A11yFinding>();
    public List<AutomationElement> Combos = new List<AutomationElement>();
    public List<AutomationElement> NavItems = new List<AutomationElement>();
    public List<AutomationElement> TabButtons = new List<AutomationElement>();
    public AutomationElement ContentHost;
    public AutomationElement TabContent;
}

public static class A11yWalk {
    static readonly Regex TypeName = new Regex(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$");
    static readonly Regex TabId = new Regex(@"^Tab[A-Z]\w*$|^\w+Tab$");

    sealed class Item {
        public AutomationElement El; public string Path; public string ParentAid;
        public Item(AutomationElement el, string path, string parentAid) { El = el; Path = path; ParentAid = parentAid; }
    }

    static bool HasGlyph(string s) {
        foreach (char ch in s) if (ch >= '\uE000' && ch <= '\uF8FF') return true;
        return false;
    }

    public static CacheRequest Request() {
        var cr = new CacheRequest();
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.IsKeyboardFocusableProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.IsEnabledProperty);
        cr.Add(AutomationElement.HelpTextProperty);
        cr.Add(AutomationElement.RuntimeIdProperty);
        cr.TreeScope = TreeScope.Subtree;
        cr.TreeFilter = Automation.ControlViewCondition;
        cr.AutomationElementMode = AutomationElementMode.Full;
        return cr;
    }

    public static A11yView Walk(AutomationElement live, string page) {
        AutomationElement root = live.GetUpdatedCache(Request());
        var v = new A11yView();
        var stack = new Stack<Item>();
        stack.Push(new Item(root, "", ""));
        while (stack.Count > 0) {
            Item it = stack.Pop();
            AutomationElement el = it.El;
            v.Count++;
            AutomationElement.AutomationElementInformation c = el.Cached;
            string name = c.Name ?? "";
            string type = c.ControlType == null ? "" : c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            string aid = c.AutomationId ?? "";
            bool empty = string.IsNullOrWhiteSpace(name);

            var rules = new List<string>();
            if (c.IsKeyboardFocusable && empty) rules.Add("FOCUSABLE_NO_NAME");
            if (!empty && HasGlyph(name)) rules.Add("GLYPH_NAME");
            if (!empty && TypeName.IsMatch(name)) rules.Add("TYPE_NAME");
            if (empty && (type == "Edit" || type == "ComboBox" || type == "Slider")) rules.Add("INPUT_NO_NAME");
            if (empty && (type == "Button" || type == "ListItem" || type == "DataItem")) rules.Add("ITEM_NO_NAME");
            if (rules.Count > 0) {
                int[] rid = (int[])el.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty);
                string ridText = rid == null ? "" : string.Join(".", rid);
                foreach (string r in rules) {
                    var f = new A11yFinding();
                    f.Page = page; f.Rule = r; f.Type = type; f.Name = name; f.Aid = aid; f.Class = c.ClassName ?? "";
                    f.Help = c.HelpText ?? ""; f.Offscreen = c.IsOffscreen; f.Path = it.Path; f.Rid = ridText;
                    v.Findings.Add(f);
                }
            }

            if (type == "ComboBox" && c.IsEnabled && !c.IsOffscreen) v.Combos.Add(el);
            if (type == "DataItem" && aid.StartsWith("Nav_", StringComparison.Ordinal)) v.NavItems.Add(el);
            if (type == "RadioButton" && TabId.IsMatch(aid) && c.IsEnabled && !c.IsOffscreen) v.TabButtons.Add(el);
            if (aid == "PART_NavigationViewContentPresenter") v.ContentHost = el;
            if (type == "Pane" && it.ParentAid == "PadPageView") v.TabContent = el;

            string label = !empty ? name : (aid.Length > 0 ? "#" + aid : (c.ClassName ?? ""));
            string childPath = it.Path.Length > 0 ? it.Path + " > " + label : label;
            AutomationElementCollection kids = el.CachedChildren;
            if (kids == null) continue;
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(new Item(kids[i], childPath, aid));
        }
        return v;
    }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$SIP = [System.Windows.Automation.SelectionItemPattern]::Pattern
$ECP = [System.Windows.Automation.ExpandCollapsePattern]::Pattern

$pf = Get-Process PadForge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $pf) { Note 'PadForge is not running. Start it with the deploy recipe first.'; Save; exit -1 }
$hwnd = $pf.MainWindowHandle
$win = $AE::FromHandle($hwnd)
$clock = [Diagnostics.Stopwatch]::StartNew()
Note "PadForge pid=$($pf.Id) hwnd=$hwnd started=$($pf.StartTime.ToString('o'))"

# The Alt tap and the attached input queue let SetForegroundWindow past the
# foreground lock (tooling memory, 2026-10-06). Skipped when PadForge is
# already in front. Restore only when minimized: SW_RESTORE would un-maximize.
function Front {
    if ([A11yWin]::GetForegroundWindow() -eq $hwnd) { return $true }
    for ($i = 0; $i -lt 5; $i++) {
        if ([A11yWin]::IsIconic($hwnd)) { [A11yWin]::ShowWindow($hwnd, 9) | Out-Null }
        [A11yWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [A11yWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        $fg = [A11yWin]::GetWindowThreadProcessId([A11yWin]::GetForegroundWindow(), [IntPtr]::Zero)
        $me = [A11yWin]::GetCurrentThreadId()
        [A11yWin]::AttachThreadInput($me, $fg, $true) | Out-Null
        [A11yWin]::BringWindowToTop($hwnd) | Out-Null
        [A11yWin]::SetForegroundWindow($hwnd) | Out-Null
        [A11yWin]::AttachThreadInput($me, $fg, $false) | Out-Null
        for ($t = 0; $t -lt 16; $t++) {
            if ([A11yWin]::GetForegroundWindow() -eq $hwnd) { return $true }
            Start-Sleep -Milliseconds 25
        }
    }
    return $false
}

function Click-Center($el) {
    $r = $el.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Height -le 0) { return $false }
    if (-not (Front)) { return $false }
    [A11yWin]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    [A11yWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [A11yWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    return $true
}

# The first child of a host, as a runtime id and a class name, read live.
function First-Child($el) {
    try {
        $ch = $Walker.GetFirstChild($el)
        if ($ch) { return @(($ch.GetRuntimeId() -join '.'), $ch.Current.ClassName) }
    } catch { }
    return @('', '')
}

# Polls every 30 ms until the condition holds, then allows 150 ms for the
# new content's bindings to fill. Returns the milliseconds waited, or -1 on
# timeout, after which the pass carries on with whatever is shown.
function Wait-For([scriptblock]$cond, [int]$timeoutMs = 4000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $cond) { Start-Sleep -Milliseconds 150; return [int]$sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 30
    }
    return -1
}

# Opens each combo box until its items exist (a closed WPF ComboBox lists no
# items, since its peer reads them through an items host that is realized
# only when the drop-down opens), then closes it.
function Open-Combos($list) {
    $n = 0
    foreach ($cb in $list) {
        try {
            $p = $cb.GetCurrentPattern($ECP)
            $p.Expand()
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 400 -and -not $Walker.GetFirstChild($cb)) { Start-Sleep -Milliseconds 15 }
            $p.Collapse()
            $n++
        } catch { }
    }
    return $n
}

$seen = @{}
$findings = New-Object System.Collections.Generic.List[object]
# Audits the view under $root: opens its combo boxes, walks it once more if
# any opened, keeps the findings not seen in an earlier view, and scans it
# with Axe when asked. Returns the walk, whose lists name the next targets.
function Audit-View($root, [string]$where) {
    $start = $clock.ElapsedMilliseconds
    $v = [A11yWalk]::Walk($root, $where)
    $opened = 0
    if (-not $NoCombos -and $v.Combos.Count -gt 0) {
        $opened = Open-Combos $v.Combos
        if ($opened -gt 0) { $v = [A11yWalk]::Walk($root, $where) }
    }
    $new = 0
    foreach ($f in $v.Findings) {
        $k = "$($f.Rid)|$($f.Rule)"
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true; $findings.Add($f); $new++
    }
    Note ("{0}: {1} elements, {2} combo boxes opened, {3} new findings, {4} ms" -f $where, $v.Count, $opened, $new, ($clock.ElapsedMilliseconds - $start))
    if ($Axe) {
        $dir = Join-Path $OutDir "axe-$stamp"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $scanId = $where -replace '[^\w\-]+', '_'
        $out = & $AxeCli --processid $pf.Id --outputdirectory $dir --scanid $scanId --verbosity verbose --alwayssavetestfile 2>&1
        $code = $LASTEXITCODE
        $out | Set-Content -Path (Join-Path $dir "$scanId.txt") -Encoding UTF8
        $summary = ($out | Where-Object { "$_" -match 'error.* found|no errors' }) -join ' '
        Note ("  axe {0}: exit {1}, {2}" -f $where, $code, $summary)
        $out | ForEach-Object { if ("$_" -match '^Error \d+: (.+)$') { $Matches[1] } } | Group-Object |
            Sort-Object Count -Descending | ForEach-Object { Note ("    {0} x {1}" -f $_.Count, $_.Name) }
    }
    return $v
}

# The whole window once, for the sidebar, the title bar and the first page.
$first = Audit-View $win 'Window'
$host_ = $first.ContentHost
if (-not $host_) { Note 'The page area (PART_NavigationViewContentPresenter) was not found.'; Save; exit -2 }
$nav = @{}
foreach ($n in $first.NavItems) { $nav[$n.Cached.AutomationId] = $n }
$tags = @($first.NavItems | ForEach-Object { $_.Cached.AutomationId.Substring(4) } | Where-Object { $_ -ne 'AddController' })
if ($Pages) { $tags = $Pages }
Note "pages: $($tags -join ', ')"

foreach ($tag in $tags) {
    $entry = $nav["Nav_$tag"]
    if (-not $entry) { Note "Nav_${tag}: not found"; continue }
    $expect = if ($tag -like 'Pad*') { 'PadPage' } else { "${tag}Page" }
    $before = First-Child $host_
    if (-not (Click-Center $entry)) { Note "Nav_${tag}: could not click"; continue }
    $sameKind = $before[1] -eq $expect -and $tag -notlike 'Pad*'
    $waited = Wait-For { $now = First-Child $host_; $now[1] -eq $expect -and ($sameKind -or $now[0] -ne $before[0]) }
    if ($waited -lt 0) { Note "${tag}: the page did not change within 4 s" }
    $page = Audit-View $host_ $tag
    if ($NoTabs -or -not $page.TabContent) { continue }

    $pane = $page.TabContent
    $tabs = $page.TabButtons
    $open = $tabs | Where-Object { try { $_.GetCurrentPattern($SIP).Current.IsSelected } catch { $false } } | Select-Object -First 1
    foreach ($tab in $tabs) {
        $tabName = $tab.Cached.Name
        $wasSelected = $false
        try { $wasSelected = $tab.GetCurrentPattern($SIP).Current.IsSelected } catch { }
        $beforeTab = First-Child $pane
        if (-not (Click-Center $tab)) { continue }
        if (-not $wasSelected) {
            $waitedTab = Wait-For { (First-Child $pane)[0] -ne $beforeTab[0] }
            if ($waitedTab -lt 0) { Note "$tag / ${tabName}: the tab content did not change within 4 s" }
        }
        Audit-View $pane "$tag / $tabName" | Out-Null
    }
    if ($open) { Click-Center $open | Out-Null }
}

$byRule = $findings | Group-Object Rule | Sort-Object Name | ForEach-Object { "{0}={1}" -f $_.Name, $_.Count }
Note ("findings: {0} ({1}), {2:N1} s" -f $findings.Count, ($byRule -join ', '), ($clock.ElapsedMilliseconds / 1000))
foreach ($f in $findings) {
    $label = if ($f.Name) { $f.Name } else { '(no name)' }
    $help = if ($f.Help) { " help='$($f.Help)'" } else { '' }
    $lines.Add(("  [{0}] {1} {2} '{3}' aid={4} class={5}{6}" -f $f.Page, $f.Rule, $f.Type, $label, $f.Aid, $f.Class, $help))
    $lines.Add(("      at {0}" -f $f.Path))
}
$lines.Add("report: $txt")
Save
$findings | ConvertTo-Json -Depth 2 -Compress | Set-Content -Path $json -Encoding UTF8
exit $findings.Count
