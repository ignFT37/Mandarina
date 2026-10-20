// Mandarina: the subtitle files of the transcription tools. FFmpeg's whisper filter writes SRT; the tools read the cues back,
// tidy what cutting the audio into chunks leaves behind (overlapping times, a fragment repeated at the seam) and write the
// TXT, SRT and VTT the user asks for, with the long cues cut to what fits on screen. Pure text work: no FFmpeg, no UI.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.App.Mandarina.Tools;

/// <summary>One subtitle: on screen from <see cref="Start"/> to <see cref="End"/>.</summary>
public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

public static partial class Subtitles
{
    /// <summary>A cue shorter than this is stretched: it could not be read.</summary>
    private static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(400);

    /// <summary>A fragment this short that only repeats the end of the cue before it is a seam of two chunks, not speech.</summary>
    private static readonly TimeSpan RepeatLength = TimeSpan.FromSeconds(1.5);

    /// <summary>A cue this long with only a word or two is not speech: over silence the model writes "Thank you." across 30 s.</summary>
    private static readonly TimeSpan SilenceFillerLength = TimeSpan.FromSeconds(20);

    /// <summary>UTF-8 with the byte order mark: Windows players and Notepad of old read a plain UTF-8 subtitle file as ANSI.</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    [GeneratedRegex(@"(?<s>\d+:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<e>\d+:\d{2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex TimeLine();

    /// <summary>Whisper's notes for stretches without speech: "[BLANK_AUDIO]", "[SILENCE]".</summary>
    [GeneratedRegex(@"^\[[A-Z_ ]{3,}\]$")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex Blocks();

    // ───────── Reading ─────────

    /// <summary>Reads an SRT file (or the cues of a WebVTT one: the time lines are the same but for the dot).</summary>
    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var normalized = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var block in Blocks().Split(normalized))
        {
            var lines = block.Split('\n');
            var at = Array.FindIndex(lines, line => TimeLine().IsMatch(line));
            if (at < 0)
                continue;
            var times = TimeLine().Match(lines[at]);
            cues.Add(new Cue(ParseTime(times.Groups["s"].Value), ParseTime(times.Groups["e"].Value),
                string.Join('\n', lines.Skip(at + 1)).Trim()));
        }
        return cues;
    }

    private static TimeSpan ParseTime(string text)
    {
        // Whole milliseconds, not a double: 5.951 s would come out as 5.9509999.
        var parts = text.Replace(',', '.').Split(':', '.');
        var milliseconds = int.Parse(parts[3].PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return new TimeSpan(0, int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture), milliseconds);
    }

    // ───────── Tidying ─────────

    /// <summary>
    /// The cues as they should be kept: no empty ones, silence notes or what the model writes over silence (a lone "." over noise,
    /// "Thank you." across half a minute of nothing), one line of text each, in time order, never overlapping, and without the
    /// fragment a chunk seam repeats. Whisper's own segments otherwise stay as they are.
    /// </summary>
    public static List<Cue> Tidy(IEnumerable<Cue> cues)
    {
        var result = new List<Cue>();
        foreach (var cue in cues.OrderBy(c => c.Start))
        {
            var text = Spaces().Replace(cue.Text, " ").Trim();
            if (text.Length == 0 || Noise().IsMatch(text))
                continue;
            var (start, end) = (cue.Start, cue.End < cue.Start ? cue.Start : cue.End);
            var words = Words(text).Count;
            if (words == 0 || (words < 4 && end - start > SilenceFillerLength))
                continue;
            if (result.Count > 0)
            {
                var previous = result[^1];
                if (end - start <= RepeatLength && RepeatsTheEnd(previous.Text, text))
                {
                    result[^1] = previous with { End = Later(previous.End, end) };
                    continue;
                }
                if (start < previous.End)
                {
                    // Chunks overlap by a few tenths of a second: the earlier cue ends where the next one begins.
                    if (start - previous.Start >= MinLength)
                        result[^1] = previous with { End = start };
                    else
                        start = previous.End;
                }
            }
            if (end - start < MinLength)
                end = start + MinLength;
            result.Add(new Cue(start, end, text));
        }
        return result;
    }

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>The words of <paramref name="text"/> are the last words of <paramref name="previous"/>.</summary>
    private static bool RepeatsTheEnd(string previous, string text)
    {
        var before = Words(previous);
        var now = Words(text);
        return now.Count > 0 && now.Count <= before.Count && before.Skip(before.Count - now.Count).SequenceEqual(now);
    }

    private static List<string> Words(string text) =>
        text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .ToList();

    // ───────── Fitting the screen ─────────

    /// <summary>
    /// Cuts the cues that are too long for the screen into several: each holds at most <paramref name="lines"/> lines of
    /// <paramref name="width"/> characters (cut at sentence ends, then commas, then spaces) and the time of the original is
    /// shared out by length. The text of every cue is wrapped with line breaks, so a subtitle reads the way it is shown.
    /// </summary>
    public static List<Cue> Fit(IEnumerable<Cue> cues, int width = 42, int lines = 2)
    {
        var result = new List<Cue>();
        foreach (var cue in cues)
        {
            var parts = Split(cue.Text, width * lines);
            var total = Math.Max(1, parts.Sum(part => part.Length));
            var at = cue.Start;
            for (var i = 0; i < parts.Count; i++)
            {
                var share = i == parts.Count - 1 ? cue.End - at : (cue.End - cue.Start) * ((double)parts[i].Length / total);
                result.Add(new Cue(at, at + share, Wrap(parts[i], width)));
                at += share;
            }
        }
        return result;
    }

    /// <summary>Pieces of at most <paramref name="capacity"/> characters, cut where a sentence or a clause ends when it can.</summary>
    private static List<string> Split(string text, int capacity)
    {
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > capacity)
        {
            var cut = CutPoint(rest, capacity);
            parts.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }
        if (rest.Length > 0)
            parts.Add(rest);
        return parts;
    }

    /// <summary>Where to cut: after the last sentence end that leaves a piece worth reading, else after a comma, else at a space.</summary>
    private static int CutPoint(string text, int capacity)
    {
        var limit = Math.Min(text.Length, capacity);
        var floor = capacity / 3; // a piece shorter than this would be a stray word on its own
        // A mark ends a clause when whitespace (or the end of the text) follows it: "12.000" does not.
        bool Ends(int i) => i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]);
        foreach (var marks in new[] { ".!?…", ",;:" })
        {
            for (var i = limit - 1; i >= floor; i--)
            {
                if (marks.Contains(text[i], StringComparison.Ordinal) && Ends(i))
                    return i + 1;
            }
        }
        var space = text.LastIndexOf(' ', Math.Min(text.Length - 1, capacity));
        return space > 0 ? space : capacity;
    }

    /// <summary>Breaks a piece of text into lines of at most <paramref name="width"/> characters, as even as it can.</summary>
    private static string Wrap(string text, int width)
    {
        if (text.Length <= width)
            return text;
        var lines = (int)Math.Ceiling(text.Length / (double)width);
        var result = new StringBuilder();
        var start = 0;
        for (var line = 1; line < lines; line++)
        {
            // The space nearest to an even split that keeps this line, and what is left, within the width.
            var ideal = (int)Math.Round(text.Length * (double)line / lines);
            var best = -1;
            for (var i = start + 1; i < text.Length && i - start <= width; i++)
            {
                if (text[i] == ' ' && text.Length - i - 1 <= width * (lines - line) && (best < 0 || Math.Abs(i - ideal) < Math.Abs(best - ideal)))
                    best = i;
            }
            if (best < 0)
                break;
            result.Append(text, start, best - start).Append('\n');
            start = best + 1;
        }
        return result.Append(text, start, text.Length - start).ToString();
    }

    // ───────── Writing ─────────

    /// <summary>"00:01:02,345" (SRT) or "00:01:02.345" (WebVTT).</summary>
    public static string Time(TimeSpan time, char decimalMark = ',') =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{decimalMark}{time.Milliseconds:000}");

    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var builder = new StringBuilder();
        var number = 0;
        foreach (var cue in cues)
            builder.Append(CultureInfo.InvariantCulture, $"{++number}\n{Time(cue.Start)} --> {Time(cue.End)}\n{cue.Text}\n\n");
        return builder.ToString();
    }

    public static string ToVtt(IEnumerable<Cue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
            builder.Append(CultureInfo.InvariantCulture, $"{Time(cue.Start, '.')} --> {Time(cue.End, '.')}\n{cue.Text}\n\n");
        return builder.ToString();
    }

    /// <summary>The text alone, one line per cue.</summary>
    public static string ToText(IEnumerable<Cue> cues) =>
        string.Concat(cues.Select(cue => Spaces().Replace(cue.Text, " ").Trim() + "\n"));
}
