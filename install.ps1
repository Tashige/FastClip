# FastClip installer: installs ffmpeg if needed, builds FastClip.exe, adds shortcuts, launches it.
# One-line install (from cmd):
#   powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/Tashige/FastClip/main/install.ps1 | iex"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoZip = 'https://github.com/Tashige/FastClip/archive/refs/heads/main.zip'
$dest = Join-Path $env:LOCALAPPDATA 'FastClip'

Write-Host '== FastClip installer ==' -ForegroundColor Cyan

# 1. ffmpeg
$haveFfmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue) -or
    (Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Gyan.FFmpeg*'))
if (-not $haveFfmpeg) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is missing. Install "App Installer" from the Microsoft Store, then run this again.' }
    Write-Host 'Installing ffmpeg...'
    winget install --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements | Out-Null
} else { Write-Host 'ffmpeg already installed.' }

# 2. source: use the folder this script is in, or download the repo
$src = $null
if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'FastClip.cs'))) { $src = $PSScriptRoot }
else {
    Write-Host 'Downloading FastClip...'
    $tmp = Join-Path $env:TEMP ('FastClip_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    Invoke-WebRequest $repoZip -OutFile "$tmp\src.zip" -UseBasicParsing
    Expand-Archive "$tmp\src.zip" $tmp
    $src = (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName
}

# 3. build
Get-Process FastClip -ErrorAction SilentlyContinue | Stop-Process -Force
# older versions could leave their recorder running; stop only FastClip's ffmpeg
Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Where-Object { $_.CommandLine -like '*FastClipBuffer*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $dest | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Write-Host 'Building FastClip.exe...'
& $csc /nologo /target:winexe /optimize+ "/out:$dest\FastClip.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$src\FastClip.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item "$src\README.md" $dest -Force -ErrorAction SilentlyContinue

# 4. shortcuts
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'FastClip.lnk'))
    $lnk.TargetPath = "$dest\FastClip.exe"
    $lnk.WorkingDirectory = $dest
    $lnk.Save()
}

Write-Host "Installed to $dest" -ForegroundColor Green
Start-Process "$dest\FastClip.exe"
