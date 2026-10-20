// Mandarina: Windows side of the file actions wheel (files on the clipboard, terminal; the share sheet is MandarinaShare), the
// frosted backdrop of the wheel (a screen capture) and its highlight tick.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class MandarinaShell
{
    // ───────── Files on the clipboard (CF_HDROP + "Preferred DropEffect" = copy) ─────────

    public static bool CopyFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;
        // DROPFILES header, then the paths as UTF-16, each NUL-terminated, and one more NUL.
        var text = string.Concat(paths.Select(p => p + "\0")) + "\0";
        var bytes = System.Text.Encoding.Unicode.GetBytes(text);
        const int header = 20;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(0))
                break;
            if (attempt == 9)
                return false;
            Thread.Sleep(20); // another program has the clipboard open for a moment
        }
        try
        {
            EmptyClipboard();
            var drop = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)(header + bytes.Length));
            var at = GlobalLock(drop);
            Marshal.WriteInt32(at, 0, header); // pFiles: offset of the list
            Marshal.WriteInt32(at, 16, 1);     // fWide: the list is UTF-16
            Marshal.Copy(bytes, 0, at + header, bytes.Length);
            GlobalUnlock(drop);
            if (SetClipboardData(CF_HDROP, drop) == 0)
                return false;

            var effect = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 4);
            Marshal.WriteInt32(GlobalLock(effect), 1); // DROPEFFECT_COPY
            GlobalUnlock(effect);
            SetClipboardData(RegisterClipboardFormat("Preferred DropEffect"), effect);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ───────── Terminal ─────────

    /// <summary>Windows Terminal in the folder, or PowerShell when Windows Terminal isn't installed.</summary>
    public static void OpenTerminal(string folder)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
        {
            var start = new ProcessStartInfo(wt) { UseShellExecute = false };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(folder);
            Process.Start(start)?.Dispose();
            return;
        }
        Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
    }

    // ───────── Screen capture for the frosted glass ─────────

    public static byte[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        var screen = GetDC(0);
        if (screen == 0)
            return null;
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0, old = 0;
        try
        {
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0)
                return null;
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY))
                return null;
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (old != 0)
                SelectObject(memory, old);
            if (bitmap != 0)
                DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // ───────── Sound ─────────

    private static readonly object SoundLock = new();
    private static byte[]? _pinnedSound;
    private static GCHandle _soundHandle;

    /// <summary>Plays a WAV held in memory. The array stays pinned while it is the current sound.</summary>
    public static void Play(byte[] wav)
    {
        lock (SoundLock)
        {
            if (!ReferenceEquals(_pinnedSound, wav))
            {
                PlaySoundW(0, 0, 0); // stop the old one before unpinning it
                if (_soundHandle.IsAllocated)
                    _soundHandle.Free();
                _soundHandle = GCHandle.Alloc(wav, GCHandleType.Pinned);
                _pinnedSound = wav;
            }
            PlaySoundW(_soundHandle.AddrOfPinnedObject(), 0, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
    }

    /// <summary>Stops the sound that is playing (the pinned array stays until the next one replaces it).</summary>
    public static void Stop()
    {
        lock (SoundLock)
            PlaySoundW(0, 0, 0);
    }

    // ───────── Native ─────────

    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x2, GMEM_ZEROINIT = 0x40;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [DllImport("winmm.dll")]
    private static extern bool PlaySoundW(nint pszSound, nint hmod, uint fdwSound);
}
