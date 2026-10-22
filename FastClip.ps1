<#
FastClip - near-zero-overhead replay-buffer clipper (Windows, NVIDIA/AMD/Intel GPU).

How it stays light:
  * Capture = DXGI Desktop Duplication (ddagrab) -> frames stay on the GPU
  * Encode  = hardware encoder (NVENC / AMF / QuickSync), CPU does almost nothing
  * Buffer  = small rolling ring of 2s .ts segments on disk (wraps, never grows)
  * Save    = stream-copy concat of the last N seconds (no re-encode, ~instant)
  * This script itself sleeps in a message loop (0% CPU) until you hit the hotkey

Usage:
  .\FastClip.ps1                         # defaults: 120s clip, 60fps, hotkey F24
  .\FastClip.ps1 -ClipSeconds 60 -Fps 60 -Bitrate 12 -Hotkey F9
  .\FastClip.ps1 -ListAudio              # show audio devices for -Audio
  .\FastClip.ps1 -Audio "VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)"
  .\FastClip.ps1 -SelfTest               # record 8s, save a clip, exit
#>
param(
    [int]$ClipSeconds = 120,
    [int]$Fps = 60,
    [int]$Bitrate = 10,             # Mbit/s
    [string]$Hotkey = 'F24',        # F1-F24 or a single letter/digit
    [string]$Modifiers = '',        # e.g. 'Ctrl+Alt' (blank = none)
    [int]$Monitor = 0,              # output index to capture
    [string]$Encoder = 'auto',      # auto | h264_nvenc | h264_amf | h264_qsv | libx264
    [string]$Audio = 'Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)', # game/desktop audio (dshow name, '' = off)
    [string]$Mic = '',             # optional mic, mixed in (dshow name)
    [string]$BufferDir = (Join-Path $env:TEMP 'FastClipBuffer'),
    [string]$OutDir = (Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Clips'),
    [switch]$ListAudio,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$SegSec = 2
function Q($a) { ($a | ForEach-Object { $s = [string]$_; if ($s -match '\s') { '"' + $s + '"' } else { $s } }) -join ' ' }

# ---- locate ffmpeg ----
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg not found on PATH (winget install Gyan.FFmpeg)' }

if ($ListAudio) {
    & $ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 | Select-String 'audio'
    return
}

# ---- pick encoder ----
function Test-Encoder($name) {
    $enc = & $ffmpeg -hide_banner -encoders 2>$null | Select-String " $name "
    if (-not $enc) { return $false }
    & $ffmpeg -hide_banner -loglevel error -f lavfi -i "color=black:s=256x256:r=30" -frames:v 2 -c:v $name -f null - 2>$null
    return ($LASTEXITCODE -eq 0)
}
if ($Encoder -eq 'auto') {
    $Encoder = 'libx264'
    foreach ($e in 'h264_nvenc', 'h264_amf', 'h264_qsv') { if (Test-Encoder $e) { $Encoder = $e; break } }
}

# ---- encoder args (low-latency, low-overhead presets) ----
$gop = $Fps * $SegSec
$br  = "${Bitrate}M"
switch ($Encoder) {
    'h264_nvenc' { $venc = @('-c:v','h264_nvenc','-preset','p4','-tune','ll','-rc','cbr','-b:v',$br,'-maxrate',$br,'-bufsize',"$($Bitrate*2)M",'-g',$gop,'-bf','0','-spatial-aq','0') }
    'h264_amf'   { $venc = @('-c:v','h264_amf','-usage','lowlatency','-quality','speed','-rc','cbr','-b:v',$br,'-g',$gop,'-bf','0') }
    'h264_qsv'   { $venc = @('-c:v','h264_qsv','-preset','veryfast','-b:v',$br,'-g',$gop,'-bf','0') }
    default      { $venc = @('-c:v','libx264','-preset','ultrafast','-tune','zerolatency','-b:v',$br,'-g',$gop,'-bf','0') }
}
$hw = $Encoder -ne 'libx264'

# ---- input args ----
# ddagrab outputs D3D11 frames. NVENC/AMF/QSV take them directly (zero CPU copy);
# libx264 needs them downloaded to system memory first.
$src = "ddagrab=output_idx=${Monitor}:framerate=${Fps}:draw_mouse=0"
$vin = @('-f','lavfi','-i',$src)
if (-not $hw) { $vin = @('-f','lavfi','-i',"$src,hwdownload,format=bgra,format=yuv420p") }
$ain = @(); $aenc = @(); $amap = @()
$adevs = @($Audio, $Mic | Where-Object { $_ })
foreach ($d in $adevs) {
    $ain += @('-thread_queue_size','512','-use_wallclock_as_timestamps','1','-f','dshow','-audio_buffer_size','50','-i',"audio=$d")
}
if ($adevs.Count -gt 0) {
    if ($adevs.Count -eq 1) { $amap = @('-map','0:v','-map','1:a','-af','aresample=async=1:first_pts=0') }
    else { $amap = @('-filter_complex','[1:a][2:a]amix=inputs=2:duration=first:normalize=0,aresample=async=1:first_pts=0[a]','-map','0:v','-map','[a]') }
    $aenc = @('-c:a','aac','-b:a','160k','-ar','48000')
}

# ---- ring buffer ----
$segCount = [math]::Ceiling($ClipSeconds / $SegSec) + 2
if (Test-Path $BufferDir) { Get-ChildItem $BufferDir -File | Remove-Item -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $BufferDir, $OutDir | Out-Null

$recArgs = @('-hide_banner','-loglevel','error','-y') + $vin + $ain + $amap + $venc + $aenc + @(
    '-f','segment','-segment_time',$SegSec,'-segment_wrap',$segCount,'-reset_timestamps','1',
    '-segment_format','mpegts',(Join-Path $BufferDir 'seg%03d.ts'))

$global:rec = Start-Process -FilePath $ffmpeg -ArgumentList (Q $recArgs) -WindowStyle Hidden -PassThru `
    -RedirectStandardError (Join-Path $BufferDir 'ffmpeg.log')
# below-normal priority so the game always wins the CPU
try { $global:rec.PriorityClass = 'BelowNormal' } catch {}
Write-Host "FastClip recording [$Encoder, ${Fps}fps, ${Bitrate}Mbps, keep last ${ClipSeconds}s]"
Write-Host "Clips -> $OutDir"

function Save-Clip {
    $segs = Get-ChildItem $BufferDir -Filter 'seg*.ts' | Where-Object Length -gt 0 | Sort-Object LastWriteTime
    if (-not $segs) { Write-Host 'Nothing buffered yet.'; return }
    $want = [math]::Ceiling($ClipSeconds / $SegSec) + 1
    $segs = $segs | Select-Object -Last $want
    $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $list = Join-Path $BufferDir "list_$stamp.txt"
    ($segs | ForEach-Object { "file '" + ($_.FullName -replace '\\','/') + "'" }) | Set-Content -Encoding ascii $list
    $out = Join-Path $OutDir "Clip_$stamp.mp4"
    # copy snapshot first so the live ring can keep wrapping while we mux
    $p = Start-Process $ffmpeg -ArgumentList (Q @('-hide_banner','-loglevel','error','-y','-f','concat','-safe','0',
        '-i',$list,'-c','copy','-movflags','+faststart',$out)) -WindowStyle Hidden -PassThru -Wait
    Remove-Item $list -Force -ErrorAction SilentlyContinue
    if ($p.ExitCode -eq 0) { [console]::Beep(1000,120); Write-Host "Saved $out" } else { [console]::Beep(300,300); Write-Host 'Save failed' }
}

function Stop-Rec { if ($global:rec -and -not $global:rec.HasExited) { try { $global:rec.Kill() } catch {} } }

if ($SelfTest) {
    Start-Sleep 8
    if ($global:rec.HasExited) { Get-Content (Join-Path $BufferDir 'ffmpeg.log'); Stop-Rec; throw 'ffmpeg died' }
    Save-Clip; Stop-Rec; return
}

# ---- global hotkey via a hidden message-only window ----
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Windows.Forms;
public class HotkeyWindow : NativeWindow, IDisposable {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    public event Action Pressed;
    public HotkeyWindow(uint mod, uint vk) {
        CreateHandle(new CreateParams());
        if (!RegisterHotKey(Handle, 1, mod | 0x4000, vk)) throw new Exception("Hotkey already in use");
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312 && Pressed != null) Pressed();
        base.WndProc(ref m);
    }
    public void Dispose() { UnregisterHotKey(Handle, 1); DestroyHandle(); }
}
'@
$mod = 0
foreach ($m in ($Modifiers -split '[+ ,]' | Where-Object { $_ })) {
    switch ($m.ToLower()) { 'alt' { $mod = $mod -bor 1 } 'ctrl' { $mod = $mod -bor 2 } 'shift' { $mod = $mod -bor 4 } 'win' { $mod = $mod -bor 8 } }
}
$vk = if ($Hotkey -match '^[Ff](\d{1,2})$') { 0x6F + [int]$Matches[1] } else { [int][char]$Hotkey.ToUpper()[0] }

$win = New-Object HotkeyWindow ([uint32]$mod), ([uint32]$vk)
$win.add_Pressed({ Save-Clip })
Write-Host "Press $Modifiers $Hotkey to save the last ${ClipSeconds}s. Ctrl+C to quit."
try {
    [System.Windows.Forms.Application]::Run()   # blocks, 0% CPU until a message arrives
} finally { $win.Dispose(); Stop-Rec }
