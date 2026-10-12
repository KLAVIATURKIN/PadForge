# The owner's settings file, guarded the same way by every script under tools
# that writes it. Each such script dot-sources this file once, near its top:
#
#     . (Join-Path $PSScriptRoot 'settings_guard.ps1')
#
# and CaptureHarnessSetupTests fails when a script that names the settings file
# does not, or when a script carries its own copy of these functions.
#
# A run that swaps the settings file for one of its own puts the owner's back at
# the end. Nothing in a run may cost the owner that file.
#
# The marker. A run writes one when it takes its backup and removes it once the
# backup is back in place. A backup found with a marker that names it is what a
# killed run left, and the next run puts it back by itself. A backup found
# without one says nothing about the settings file.
# Every run used to restore such a backup on sight, so one left behind after
# the settings were put back by hand would have replaced every setting changed
# since. Such a run stops before it changes anything, and the owner says which
# file to keep (-KeepSettingsFrom). A marker older than BackupMarkerMaxAgeHours
# counts as none, because a hand restore leaves the marker where it is. Two
# files with the same bytes need no decision. Each backup has its own marker,
# named after the backup's path, so scripts that keep their backups in
# different places never speak for each other's.
#
# The copies. A settings file is copied into the capture folder, under a name
# that carries the time in UTC, before a run overwrites or deletes it, so no
# step destroys the only copy of one.
#
# Each function stops the script that called it when anything in it fails,
# whatever ErrorActionPreference that script runs with, so a copy that failed
# can never be followed by the write it was taken for.
#
# ASCII only: Windows PowerShell 5.1 reads a file with no byte order mark as
# ANSI.
$script:CaptureStateDir = Join-Path $env:TEMP "PadForge_Capture"
$script:BackupMarkerMaxAgeHours = 12
$script:SettingsCopied = @()
# The script that dot-sourced this file, for the marker. Inside a function
# here, $PSCommandPath names this file instead.
$script:SettingsGuardCaller = $MyInvocation.PSCommandPath

# PadForge writes its settings as UTF-8 with no byte order mark
# (SettingsService.SaveToFile, XmlSerializer into a stream). Windows PowerShell
# 5.1 reads such a file through Get-Content as ANSI, so a device or profile
# name with a letter outside ASCII came back as two or three wrong ones and
# went into the capture file that way. XmlDocument reads the encoding the file
# declares. The stream is shared for writing, as Get-Content's is, so a read
# while PadForge runs cannot make PadForge's own save fail. The path is
# resolved the way the cmdlets resolve one, from PowerShell's location, where
# .NET would start from the process's.
function Read-SettingsXml {
    param([string]$Path)
    $ErrorActionPreference = 'Stop'
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $doc = New-Object System.Xml.XmlDocument
    $stream = New-Object System.IO.FileStream($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { $doc.Load($stream) } finally { $stream.Dispose() }
    return $doc
}

# Saves a settings document the way PadForge writes one: UTF-8, no byte order
# mark, two-space indents. XmlDocument.Save(path) adds the mark. The file it
# replaces is copied aside first, and that copy's path is what comes back,
# or nothing when no file was there.
function Save-SettingsXml {
    param([System.Xml.XmlDocument]$Doc, [string]$Path)
    $ErrorActionPreference = 'Stop'
    $kept = Save-SettingsCopy $Path
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $writer = New-Object System.Xml.XmlTextWriter($full, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $writer.Formatting = [System.Xml.Formatting]::Indented
        $Doc.Save($writer)
    } finally { $writer.Close() }
    return $kept
}

# Copies a settings file into the capture folder before a run overwrites or
# deletes it, and returns the copy's path, or nothing when no file is there.
# The first copy a run takes of a path is of a file it found, which may be the
# owner's: the newest fourteen of those are kept. A later copy of the same
# path is of a file the run wrote itself, and the newest six of those are
# kept, so a run's own capture files never push the owner's out. That makes
# twenty at most. The time in a name is UTC, so the names sort in the order
# the copies were taken: a local time runs through one hour twice when the
# clocks go back.
function Save-SettingsCopy {
    param([string]$Path)
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-Path -LiteralPath $script:CaptureStateDir)) { New-Item -ItemType Directory -Path $script:CaptureStateDir | Out-Null }
    $full = Convert-Path -LiteralPath $Path
    $kind = if ($script:SettingsCopied -contains $full) { "written" } else { "found" }
    $stamp = [datetime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'", [cultureinfo]::InvariantCulture)
    $copy = Join-Path $script:CaptureStateDir ("settings-{0}-{1}-{2}" -f $kind, $stamp, [System.IO.Path]::GetFileName($full))
    Copy-Item -LiteralPath $full -Destination $copy -Force
    $script:SettingsCopied += $full
    $keep = if ($kind -eq "found") { 14 } else { 6 }
    $old = @(Get-ChildItem -LiteralPath $script:CaptureStateDir -Filter "settings-$kind-*" | Sort-Object Name -Descending | Select-Object -Skip $keep)
    foreach ($o in $old) { Remove-Item -LiteralPath $o.FullName -Force -ErrorAction SilentlyContinue }
    return $copy
}

# Where the marker of one backup lives: named after the backup's full path, so
# two scripts that keep their backup in the same place share one marker and no
# two backups do.
function Get-BackupMarkerPath {
    param([string]$BackupPath)
    $ErrorActionPreference = 'Stop'
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupPath)
    $name = $full -replace '[\\/:*?"<>|]', '_'
    return (Join-Path $script:CaptureStateDir ("settings-backup-marker-" + $name + ".json"))
}

# Records the backup a run has just taken: its path, the time and the script.
# A run that had no settings file to back up takes no backup and gets no
# marker.
function Set-BackupMarker {
    param([string]$BackupPath)
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path -LiteralPath $BackupPath)) { return }
    if (-not (Test-Path -LiteralPath $script:CaptureStateDir)) { New-Item -ItemType Directory -Path $script:CaptureStateDir | Out-Null }
    [ordered]@{
        Backup   = (Convert-Path -LiteralPath $BackupPath)
        TakenUtc = [datetime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss", [cultureinfo]::InvariantCulture)
        Script   = [System.IO.Path]::GetFileName([string]$script:SettingsGuardCaller)
    } | ConvertTo-Json | Set-Content -LiteralPath (Get-BackupMarkerPath $BackupPath) -Encoding utf8
}

function Remove-BackupMarker {
    param([string]$BackupPath)
    $ErrorActionPreference = 'Stop'
    Remove-Item -LiteralPath (Get-BackupMarkerPath $BackupPath) -Force -ErrorAction SilentlyContinue
}

# True when two settings files hold the same bytes. This runs while PadForge
# may still be up, so each file is read through a stream shared for writing,
# as Read-SettingsXml reads. Get-FileHash opens a file for reading alone, and
# an autosave PadForge made in that moment was refused and not tried again
# until the next change. A file that cannot be read stops the run.
function Test-SameSettings {
    param([string]$A, [string]$B)
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path -LiteralPath $A) -or -not (Test-Path -LiteralPath $B)) { return $false }
    if ((Get-Item -LiteralPath $A).Length -ne (Get-Item -LiteralPath $B).Length) { return $false }
    $hashes = @(foreach ($p in @($A, $B)) {
        $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($p)
        $sha = New-Object System.Security.Cryptography.SHA256CryptoServiceProvider
        $stream = New-Object System.IO.FileStream($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { (@($sha.ComputeHash($stream) | ForEach-Object { $_.ToString("x2") })) -join "" }
        finally { $stream.Dispose(); $sha.Dispose() }
    })
    return ($hashes[0] -eq $hashes[1])
}

# Says why a backup an earlier run left is not known to hold the settings to
# put back, or returns an empty string when it is known: the owner
# said which file to keep, the two files are the same, or a marker no older
# than BackupMarkerMaxAgeHours names the backup. No backup, no doubt.
function Get-BackupDoubt {
    param([string]$SettingsPath, [string]$BackupPath, [string]$Keep)
    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path -LiteralPath $BackupPath)) { return "" }
    if ($Keep -eq "File" -or $Keep -eq "Backup") { return "" }
    if (Test-SameSettings $SettingsPath $BackupPath) { return "" }
    $markerPath = Get-BackupMarkerPath $BackupPath
    if (-not (Test-Path -LiteralPath $markerPath)) { return "no run left a marker for it" }
    $marker = $null
    try { $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json } catch { }
    # One name at a time: under StrictMode .Properties.Name throws for an
    # object that has no properties at all.
    $names = @()
    if ($null -ne $marker) { $names = @($marker.PSObject.Properties | ForEach-Object { $_.Name }) }
    if ($names -notcontains "Backup" -or $names -notcontains "TakenUtc") { return "its marker in $script:CaptureStateDir cannot be read" }
    if ((Convert-Path -LiteralPath $BackupPath) -ne [string]$marker.Backup) { return "its marker names another backup, $($marker.Backup)" }
    $taken = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [datetime]::TryParseExact([string]$marker.TakenUtc, "yyyy-MM-dd HH:mm:ss", [cultureinfo]::InvariantCulture, $styles, [ref]$taken)) {
        return "the time in its marker cannot be read"
    }
    if (([datetime]::UtcNow - $taken).TotalHours -gt $script:BackupMarkerMaxAgeHours) {
        return ("its marker is from {0}, more than {1} hours ago" -f $taken.ToLocalTime().ToString("yyyy-MM-dd HH:mm", [cultureinfo]::InvariantCulture), $script:BackupMarkerMaxAgeHours)
    }
    return ""
}

# The lines a run prints when it stops for the owner's decision.
function Get-BackupStopLines {
    param([string]$SettingsPath, [string]$BackupPath, [string]$Doubt)
    $ErrorActionPreference = 'Stop'
    $describe = {
        param($p)
        if (Test-Path -LiteralPath $p) {
            $item = Get-Item -LiteralPath $p
            "{0}, last written {1}, {2} bytes" -f $p, $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", [cultureinfo]::InvariantCulture), $item.Length
        } else { "$p, which is not there" }
    }
    return @(
        "!! STOPPED with neither file touched. A backup of the settings file is still there, and $Doubt.",
        "!!   settings file: $(& $describe $SettingsPath)",
        "!!   backup:        $(& $describe $BackupPath)",
        "!! One of the two holds the settings to keep, and the run cannot tell which. That is for you to decide:",
        "!!   -KeepSettingsFrom File    the settings file holds them. The backup is set aside.",
        "!!   -KeepSettingsFrom Backup  the backup holds them. It goes back over the settings file.",
        "!! Run again with one of the two. The file that gives way is first copied to $script:CaptureStateDir."
    )
}
