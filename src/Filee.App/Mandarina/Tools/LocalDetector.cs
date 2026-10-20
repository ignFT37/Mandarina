// Mandarina: finds text and faces in a picture, all on this PC, for the REDACT tools. Windows already ships both as WinRT
// APIs (Windows.Media.Ocr.OcrEngine, Windows.Media.FaceAnalysis.FaceDetector), but this project is plain net10.0 without
// the WinRT projection, so Windows PowerShell 5.1 makes the calls: the script below goes over the command line
// (-EncodedCommand: no .ps1 on disk, no execution-policy change), reads the picture from an environment variable and
// prints one line of JSON with the rectangles. Nothing is downloaded and nothing leaves the machine. Whatever is missing
// (PowerShell, WinRT, the OCR language, the face detector) ends as an empty result plus a reason, never as an exception.

using System.ComponentModel;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Filee.Engines.Infrastructure;

namespace Filee.App.Mandarina.Tools;

/// <summary>A rectangle found in the picture, in pixels of the picture that was analysed.</summary>
public readonly record struct DetectedRegion(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>Why a detection found nothing although it should have worked.</summary>
public enum RedactDetectionFailure
{
    /// <summary>It ran; an empty list then simply means there was nothing to find.</summary>
    None,

    /// <summary>Not Windows, or no Windows PowerShell 5.1 to call the Windows APIs from.</summary>
    Unavailable,

    /// <summary>Windows has no text-recognition language installed.</summary>
    NoLanguage,

    /// <summary>This Windows has no face detector.</summary>
    NoFaceDetector,

    /// <summary>It did not finish in time.</summary>
    TimedOut,

    /// <summary>Anything else: the picture could not be read, WinRT refused, …</summary>
    Failed,
}

/// <summary>
/// What a detection found: the rectangles (this object is that list) and, when it could not run, why. An empty list with
/// <see cref="Failure"/> = <see cref="RedactDetectionFailure.None"/> means the picture has nothing of that kind.
/// </summary>
public sealed class RedactDetectionResult : IReadOnlyList<DetectedRegion>
{
    private readonly IReadOnlyList<DetectedRegion> _regions;

    private RedactDetectionResult(IReadOnlyList<DetectedRegion> regions, RedactDetectionFailure failure, string? reason, string? language = null)
    {
        _regions = regions;
        Failure = failure;
        Reason = reason;
        Language = language;
    }

    /// <summary>For text: the language Windows read with ("es-ES"), the first language of the user's profile that has a recognizer; null for faces.</summary>
    public string? Language { get; }

    public RedactDetectionFailure Failure { get; }

    /// <summary>A short English sentence saying why nothing was detected ("Windows has no text-recognition language installed."); null when it ran.</summary>
    public string? Reason { get; }

    public bool Succeeded => Failure == RedactDetectionFailure.None;

    public int Count => _regions.Count;

    public DetectedRegion this[int index] => _regions[index];

    public IEnumerator<DetectedRegion> GetEnumerator() => _regions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static RedactDetectionResult Found(IReadOnlyList<DetectedRegion> regions, string? language = null) =>
        new(regions, RedactDetectionFailure.None, null, language);

    internal static RedactDetectionResult Unable(RedactDetectionFailure failure, string reason) => new([], failure, reason);
}

public static class LocalDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>A byte order mark some consoles put before the first line.</summary>
    private const char Bom = (char)0xFEFF;

    /// <summary>
    /// Whether the pieces this needs are there: Windows and its PowerShell. A cheap check; the OCR language and the face
    /// detector can still be missing, which shows as a <see cref="RedactDetectionFailure"/> when a detection runs.
    /// </summary>
    public static bool IsAvailable => PowerShellPath() is not null;

    /// <summary>
    /// One rectangle per phrase of text in the PNG at <paramref name="pngPath"/> (the words of a line that sit close
    /// together are joined), each with a little margin, in pixels of that PNG.
    /// </summary>
    public static async Task<RedactDetectionResult> FindTextAsync(string pngPath, CancellationToken cancel = default)
    {
        var raw = await RunAsync("text", pngPath, cancel);
        return raw.Result ?? RedactDetectionResult.Found(Phrases(raw.Boxes, raw.Width, raw.Height), raw.Language);
    }

    /// <summary>One rectangle per face in the PNG at <paramref name="pngPath"/>, widened by about 15 % to cover hair and chin.</summary>
    public static async Task<RedactDetectionResult> FindFacesAsync(string pngPath, CancellationToken cancel = default)
    {
        var raw = await RunAsync("faces", pngPath, cancel);
        return raw.Result ?? RedactDetectionResult.Found(Faces(raw.Boxes, raw.Width, raw.Height));
    }

    // ───────── Shaping what Windows returns ─────────

    /// <summary>
    /// Windows gives the face itself, tight; hair and chin need room, so every side grows by 15 % of the face. A face that more than
    /// one crop of the picture saw (see the script) counts once.
    /// </summary>
    internal static List<DetectedRegion> Faces(IReadOnlyList<RawBox> faces, double width, double height) =>
        MergeFaces(faces)
            .Select(face => Grow(new DetectedRegion(face.X, face.Y, face.Width, face.Height), face.Width * 0.15, face.Height * 0.15, width, height))
            .ToList();

    /// <summary>
    /// The faces without repeats: the bigger box of each pair that is the same face wins (the whole picture and a crop of it, or two
    /// crops that overlap, see the same face with slightly different boxes, and a crop's edge can cut one in two).
    /// </summary>
    internal static List<RawBox> MergeFaces(IReadOnlyList<RawBox> boxes)
    {
        var kept = new List<RawBox>();
        foreach (var box in boxes.OrderByDescending(box => box.Width * box.Height))
        {
            if (!kept.Any(big => SameFace(big, box)))
                kept.Add(box);
        }
        return kept;
    }

    /// <summary>True when <paramref name="small"/> is mostly inside <paramref name="big"/> or its centre is: two faces next to each other only touch.</summary>
    private static bool SameFace(RawBox big, RawBox small)
    {
        var across = Math.Max(0, Math.Min(big.X + big.Width, small.X + small.Width) - Math.Max(big.X, small.X));
        var down = Math.Max(0, Math.Min(big.Y + big.Height, small.Y + small.Height) - Math.Max(big.Y, small.Y));
        var smaller = Math.Min(big.Width * big.Height, small.Width * small.Height);
        if (smaller > 0 && across * down / smaller >= 0.4)
            return true;
        var (centreX, centreY) = (small.X + small.Width / 2, small.Y + small.Height / 2);
        return centreX >= big.X && centreX <= big.X + big.Width && centreY >= big.Y && centreY <= big.Y + big.Height;
    }

    /// <summary>
    /// Windows returns words; a rectangle per word is too many to handle by hand and leaves slits between them, so the
    /// words of a line that follow each other closely become one rectangle. A gap wider than a line of text (a table,
    /// two columns) starts a new one.
    /// </summary>
    internal static List<DetectedRegion> Phrases(IReadOnlyList<RawBox> words, double width, double height)
    {
        var phrases = new List<DetectedRegion>();
        foreach (var line in words.GroupBy(word => word.Line).OrderBy(group => group.Key))
        {
            DetectedRegion? current = null;
            foreach (var word in line.OrderBy(word => word.X))
            {
                var box = new DetectedRegion(word.X, word.Y, word.Width, word.Height);
                if (current is { } phrase && box.X - phrase.Right <= 1.2 * Math.Max(phrase.Height, box.Height))
                {
                    current = Union(phrase, box);
                    continue;
                }
                if (current is { } finished)
                    phrases.Add(finished);
                current = box;
            }
            if (current is { } last)
                phrases.Add(last);
        }
        return phrases
            .Select(phrase =>
            {
                var margin = Math.Max(2, phrase.Height * 0.12);
                return Grow(phrase, margin, margin, width, height);
            })
            .ToList();
    }

    private static DetectedRegion Union(DetectedRegion a, DetectedRegion b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new DetectedRegion(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>The rectangle grown by the margins on every side, kept inside the picture.</summary>
    private static DetectedRegion Grow(DetectedRegion box, double marginX, double marginY, double width, double height)
    {
        var left = Math.Max(0, box.X - marginX);
        var top = Math.Max(0, box.Y - marginY);
        var right = Math.Min(width > 0 ? width : double.MaxValue, box.Right + marginX);
        var bottom = Math.Min(height > 0 ? height : double.MaxValue, box.Bottom + marginY);
        return new DetectedRegion(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    // ───────── Calling Windows ─────────

    /// <summary>A word or face as Windows reports it, before any margin; <paramref name="Line"/> groups the words of a line.</summary>
    internal readonly record struct RawBox(int Line, double X, double Y, double Width, double Height);

    private sealed record Raw(RedactDetectionResult? Result, int Width, int Height, IReadOnlyList<RawBox> Boxes, string? Language = null);

    private static Raw Unable(RedactDetectionFailure failure, string reason) => new(RedactDetectionResult.Unable(failure, reason), 0, 0, []);

    private static string? PowerShellPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    private static async Task<Raw> RunAsync(string mode, string pngPath, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (PowerShellPath() is not { } powershell)
            return Unable(RedactDetectionFailure.Unavailable, "Windows PowerShell is not available here.");
        if (!File.Exists(pngPath))
            return Unable(RedactDetectionFailure.Failed, "The picture to analyse is missing.");

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-NoLogo", "-InputFormat", "None", "-EncodedCommand", EncodedScript],
                Timeout,
                cancel,
                environment: new Dictionary<string, string>
                {
                    ["MANDARINA_DETECT_MODE"] = mode,
                    ["MANDARINA_DETECT_IMAGE"] = Path.GetFullPath(pngPath),
                });
        }
        catch (TimeoutException)
        {
            return Unable(RedactDetectionFailure.TimedOut, "Windows took too long to analyse the picture.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Unable(RedactDetectionFailure.Unavailable, "Windows PowerShell could not be started: " + ex.Message);
        }

        // The script prints one line of JSON; anything else on stdout (a profile banner, a warning) is ignored.
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(text => text.TrimStart(Bom).StartsWith('{'));
        if (line is null)
        {
            var detail = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return Unable(RedactDetectionFailure.Failed, "Windows could not analyse the picture" + (detail is null ? "." : ": " + detail));
        }
        try
        {
            return Parse(line.TrimStart(Bom));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Unable(RedactDetectionFailure.Failed, "Windows answered something unexpected.");
        }
    }

    private static Raw Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
            return code switch
            {
                "no_winrt" => Unable(RedactDetectionFailure.Unavailable, "This Windows does not offer its picture analysis to PowerShell."),
                "no_language" => Unable(RedactDetectionFailure.NoLanguage, "Windows has no text-recognition language installed."),
                "no_face_detector" => Unable(RedactDetectionFailure.NoFaceDetector, "This Windows has no face detector."),
                "too_big" => Unable(RedactDetectionFailure.Failed, "The picture is too large for Windows to analyse."),
                "timeout" => Unable(RedactDetectionFailure.TimedOut, "Windows took too long to analyse the picture."),
                _ => Unable(RedactDetectionFailure.Failed, "Windows could not analyse the picture."),
            };
        }
        var boxes = new List<RawBox>();
        foreach (var item in root.GetProperty("r").EnumerateArray())
        {
            var numbers = item.EnumerateArray().Select(number => number.GetDouble()).ToArray();
            if (numbers.Length == 5 && numbers[3] > 0 && numbers[4] > 0)
                boxes.Add(new RawBox((int)numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]));
        }
        var language = root.TryGetProperty("lang", out var languageElement) ? languageElement.GetString() : null;
        return new Raw(null, root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32(), boxes, string.IsNullOrEmpty(language) ? null : language);
    }

    private static string EncodedScript { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    /// <summary>
    /// The PowerShell the detections run. Input: MANDARINA_DETECT_MODE ("text" | "faces") and MANDARINA_DETECT_IMAGE (the
    /// path). Output: <c>{"ok":true,"w":W,"h":H,"r":[[line,x,y,width,height],…]}</c> or <c>{"ok":false,"code":"…"}</c>.
    /// </summary>
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $mode = $env:MANDARINA_DETECT_MODE
        $path = $env:MANDARINA_DETECT_IMAGE
        $ci = [System.Globalization.CultureInfo]::InvariantCulture

        function Finish([string]$json) {
            [Console]::Out.WriteLine($json)
            exit 0
        }
        function Fail([string]$code) {
            Finish ('{"ok":false,"code":"' + $code + '"}')
        }

        # The WinRT types: only what this mode needs, so a Windows without faces still reads text.
        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            if ($mode -eq 'faces') {
                $null = [Windows.Media.FaceAnalysis.FaceDetector, Windows.Foundation, ContentType = WindowsRuntime]
                $null = [Windows.Media.FaceAnalysis.DetectedFace, Windows.Foundation, ContentType = WindowsRuntime]
            } else {
                $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
            }
        } catch {
            if ($mode -eq 'faces') { Fail 'no_face_detector' } else { Fail 'no_winrt' }
        }

        # WinRT asynchronous operations as .NET tasks, waited on here.
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } | Select-Object -First 1
        function Await($operation, [Type]$resultType) {
            $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
            if (-not $task.Wait(45000)) { throw 'timeout' }
            $task.Result
        }

        # The picture as a SoftwareBitmap, read through a .NET stream (no StorageFile: it refuses some folders).
        try {
            $stream = [System.IO.File]::OpenRead($path)
            $random = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($stream)
            $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($random)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        } catch {
            Fail 'decode'
        }

        $lang = ''
        $out = New-Object System.Text.StringBuilder
        [void]$out.Append('{"ok":true,"w":').Append($bitmap.PixelWidth.ToString($ci)).Append(',"h":').Append($bitmap.PixelHeight.ToString($ci)).Append(',"r":[')
        $first = $true
        function Add-Box($line, $x, $y, $w, $h) {
            if (-not $script:first) { [void]$script:out.Append(',') }
            $script:first = $false
            [void]$script:out.Append(('[{0},{1},{2},{3},{4}]' -f [int]$line, [int][math]::Round($x), [int][math]::Round($y), [int][math]::Round($w), [int][math]::Round($h)))
        }

        try {
            if ($mode -eq 'faces') {
                if (-not [Windows.Media.FaceAnalysis.FaceDetector]::IsSupported) { Fail 'no_face_detector' }
                $detector = Await ([Windows.Media.FaceAnalysis.FaceDetector]::CreateAsync()) ([Windows.Media.FaceAnalysis.FaceDetector])
                $script:n = 0
                function Find-Faces($picture, $offsetX, $offsetY) {
                    # The detector reads grey pictures.
                    $grey = [Windows.Graphics.Imaging.SoftwareBitmap]::Convert($picture, [Windows.Graphics.Imaging.BitmapPixelFormat]::Gray8)
                    $faces = Await ($detector.DetectFacesAsync($grey)) ([System.Collections.Generic.IList[Windows.Media.FaceAnalysis.DetectedFace]])
                    foreach ($face in $faces) {
                        $box = $face.FaceBox
                        Add-Box $script:n ($offsetX + $box.X) ($offsetY + $box.Y) $box.Width $box.Height
                        $script:n++
                    }
                }
                # A crop of the picture at its own size, decoded again from the file.
                function Crop-Picture($x, $y, $cropWidth, $cropHeight) {
                    $transform = New-Object Windows.Graphics.Imaging.BitmapTransform
                    $bounds = New-Object Windows.Graphics.Imaging.BitmapBounds
                    $bounds.X = [uint32]$x; $bounds.Y = [uint32]$y; $bounds.Width = [uint32]$cropWidth; $bounds.Height = [uint32]$cropHeight
                    $transform.Bounds = $bounds
                    $transform.ScaledWidth = [uint32]$decoder.PixelWidth; $transform.ScaledHeight = [uint32]$decoder.PixelHeight
                    Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied, $transform, [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation, [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
                }
                # Where crops of a given size start along one side so that they cover it, one step apart at most.
                function Crop-Starts($total, $size, $step) {
                    if ($total -le $size) { return @(0) }
                    $steps = [int][Math]::Ceiling(($total - $size) / $step)
                    return @(0..$steps | ForEach-Object { [int][Math]::Round($_ * ($total - $size) / $steps) })
                }
                Find-Faces $bitmap 0 0
                # The detector misses a face smaller than about 4.5 % of the picture's long side (and than 24 px): in a group photo
                # most are. A crop has a shorter long side, so the same faces are big enough in it. Crops of 0.6 and 0.36 of the long
                # side, overlapping by more than the faces that each size is for; the same face seen twice is merged by the caller.
                $width = [int]$bitmap.PixelWidth; $height = [int]$bitmap.PixelHeight
                $long = [Math]::Max($width, $height)
                foreach ($plan in @(@(0.6, 0.5), @(0.36, 0.3))) {
                    $side = [int][Math]::Round($long * $plan[0])
                    if ($side -lt 320) { continue }
                    $cropWidth = [Math]::Min($side, $width); $cropHeight = [Math]::Min($side, $height)
                    $xs = Crop-Starts $width $cropWidth ([int][Math]::Round($long * $plan[1]))
                    $ys = Crop-Starts $height $cropHeight ([int][Math]::Round($long * $plan[1]))
                    foreach ($cy in $ys) {
                        foreach ($cx in $xs) {
                            try { Find-Faces (Crop-Picture $cx $cy $cropWidth $cropHeight) $cx $cy } catch { }
                        }
                    }
                }
            } else {
                # The user's own languages first, then whichever recognizer Windows has.
                $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
                if (-not $engine) {
                    $language = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Select-Object -First 1
                    if ($language) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language) }
                }
                if (-not $engine) { Fail 'no_language' }
                $lang = $engine.RecognizerLanguage.LanguageTag
                $limit = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
                if ($bitmap.PixelWidth -gt $limit -or $bitmap.PixelHeight -gt $limit) { Fail 'too_big' }
                $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $n = 0
                foreach ($line in $result.Lines) {
                    foreach ($word in $line.Words) {
                        $r = $word.BoundingRect
                        Add-Box $n $r.X $r.Y $r.Width $r.Height
                    }
                    $n++
                }
            }
        } catch {
            if ($_.Exception.Message -eq 'timeout') { Fail 'timeout' } else { Fail 'failed' }
        }
        [void]$out.Append('],"lang":"').Append($lang).Append('"}')
        Finish $out.ToString()
        """;
}
