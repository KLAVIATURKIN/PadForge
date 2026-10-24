# Renders tools/readme_header.html to screenshots/header.png, the README's
# header banner, at twice its CSS size (2560 x 720) with clear corners.
# Headless Edge waits up to 10 s of page time for the Google Fonts the page
# loads. Its temporary profile goes in %TEMP% and is deleted afterward.
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$page = Join-Path $repo "tools\readme_header.html"
$out = Join-Path $repo "screenshots\header.png"
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$profileDir = Join-Path $env:TEMP "PadForge_ReadmeHeader"
$url = "file:///" + ($page -replace '\\', '/')
Start-Process $edge -Wait -ArgumentList @(
    "--headless=new", "--disable-gpu", "--hide-scrollbars",
    "--user-data-dir=`"$profileDir`"",
    "--force-device-scale-factor=2", "--window-size=1280,360",
    "--default-background-color=00000000", "--virtual-time-budget=10000",
    "--screenshot=`"$out`"", $url)
Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($out)
Write-Host ("{0}: {1} x {2}, {3} KB" -f $out, $img.Width, $img.Height, [math]::Round((Get-Item $out).Length / 1024))
$img.Dispose()
