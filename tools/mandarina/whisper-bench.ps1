# Times Whisper (the FFmpeg `whisper` filter with Mandarina's model) on one audio or video file, on the GPU or on the CPU, and
# prints the speed as a multiple of real time.
# -GpuDevice picks the Vulkan device (0 is whichever Windows lists first, often an integrated GPU that writes garbage; the dedicated card is usually 1).
# -Model is any ggml file (the large one by default; the small one is engines\whisper-small\ggml-small-q8_0.bin). On the CPU (-Gpu 0) the
# app runs the filter with -FilterThreads <physical cores> (WhisperEngine.Arguments): with 0 FFmpeg starts one thread per logical processor,
# which on a CPU with two threads per core is up to 30 % slower. The app's queue is 120 s.
# Usage: powershell -File whisper-bench.ps1 -Audio voz.wav [-Gpu 1|0] [-GpuDevice 0] [-Queue 20] [-Language auto] [-FilterThreads 0] [-Vad <modelo>] [-Model <archivo.bin>]
param(
    [Parameter(Mandatory = $true)][string]$Audio,
    [int]$Gpu = 1,
    [int]$GpuDevice = 0,
    [int]$Queue = 20,
    [string]$Language = 'auto',
    [int]$FilterThreads = 0,
    [string]$Vad = '',
    [string]$Model = (Join-Path $env:USERPROFILE '.mandarina\engines\whisper\ggml-large-v3-turbo-q5_0.bin'),
    [string]$Ffmpeg = (Join-Path $env:USERPROFILE '.mandarina\engines\ffmpeg\bin\ffmpeg.exe'),
    [string]$Out = ''
)
function Esc([string]$p) { "'" + ($p -replace '\\', '/' -replace ':', '\:') + "'" }
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) ('whisper-bench-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.srt') }
$graph = "whisper=model=$(Esc $Model):language=${Language}:queue=${Queue}:use_gpu=$(if ($Gpu) { 'true' } else { 'false' }):gpu_device=${GpuDevice}:destination=$(Esc $Out):format=srt"
if ($Vad) { $graph += ":vad_model=$(Esc $Vad)" }
$graphFile = Join-Path ([IO.Path]::GetTempPath()) ('whisper-graph-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.txt')
Set-Content -Path $graphFile -Value $graph -Encoding ascii -NoNewline
$duration = & (Join-Path (Split-Path $Ffmpeg) 'ffprobe.exe') -v error -show_entries format=duration -of csv=p=0 $Audio
$arguments = @('-hide_banner', '-nostdin', '-v', 'info', '-y')
if ($FilterThreads -gt 0) { $arguments += @('-filter_threads', $FilterThreads) }
$arguments += @('-i', $Audio, '-vn', '-/af', $graphFile, '-f', 'null', '-')
$watch = [Diagnostics.Stopwatch]::StartNew()
$log = & $Ffmpeg @arguments 2>&1 | ForEach-Object { "$_" }
$watch.Stop()
Remove-Item $graphFile -ErrorAction SilentlyContinue
$seconds = [double]::Parse($duration, [Globalization.CultureInfo]::InvariantCulture)
$backend = ($log | Where-Object { $_ -match 'ggml_vulkan|ggml_cuda|CUDA|Vulkan|whisper_backend|using .* backend|GPU' } | Select-Object -First 3) -join ' | '
'audio: {0:N1} s | gpu={1} device={7} queue={2} threads={3} | took {4:N1} s | {5:N2}x real time | backend: {6}' -f $seconds, $Gpu, $Queue, $FilterThreads, $watch.Elapsed.TotalSeconds, ($seconds / $watch.Elapsed.TotalSeconds), $backend, $GpuDevice
"output: $Out"
