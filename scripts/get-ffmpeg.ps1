# Downloads the FFmpeg (LGPL) build that gets bundled inside TailPad.exe for remote play.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $root 'pc/TailPad.Host/Engine'
New-Item -ItemType Directory -Force $engine | Out-Null
$zip = Join-Path $env:TEMP 'ffmpeg-lgpl.zip'
$urls = @(
  'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-8.1.zip',
  'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip'
)
foreach ($u in $urls) {
  try { Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing; Write-Host "Downloaded $u"; break } catch { Write-Host "Failed $u" }
}
$tmp = Join-Path $env:TEMP 'ffmpeg-lgpl'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive $zip -DestinationPath $tmp
$exe = Get-ChildItem -Recurse $tmp -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $engine 'ffmpeg.exe') -Force
& (Join-Path $engine 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
