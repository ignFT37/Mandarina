// Mandarina: the few Win32 calls the Microsoft Office engine needs and .NET doesn't have: the windows of a process (to
// see a dialog Office is stuck on), the command line of a process (to tell the Office that COM started for us from one the
// user opened), a job object that closes our Office processes if Mandarina dies, and Windows' licensing status for Office
// (to say "not activated" instead of waiting for a Word that never saves).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Filee.Engines.Office.Com;

/// <summary>How Windows says the installed Office is licensed.</summary>
internal enum OfficeLicense
{
    /// <summary>No license information (Microsoft 365 by account, or the API is missing): assume it works.</summary>
    Unknown,

    /// <summary>A license that is activated, or in its grace period.</summary>
    Licensed,

    /// <summary>Installed licenses only in "notification" state: Office runs in reduced functionality mode.</summary>
    Restricted,
}

[SupportedOSPlatform("windows")]
internal static class OfficeNative
{
    // ───────────────────────── Windows of a process ─────────────────────────

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);

    private const uint GwOwner = 4;

    /// <summary>A top-level window.</summary>
    /// <param name="Modal">It has no owner, or its owner is disabled: it stops the application until someone answers it
    /// (a progress window that lets the application go on has an enabled owner).</param>
    internal readonly record struct WindowInfo(IntPtr Handle, bool Visible, string ClassName, string Title, bool Modal);

    /// <summary>Every top-level window of process <paramref name="processId"/>, visible or not.</summary>
    public static List<WindowInfo> WindowsOf(int processId)
    {
        var windows = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var windowProcess);
            if (windowProcess != (uint)processId)
                return true;
            var className = new StringBuilder(256);
            var title = new StringBuilder(512);
            GetClassName(window, className, className.Capacity);
            GetWindowText(window, title, title.Capacity);
            var owner = GetWindow(window, GwOwner);
            windows.Add(new WindowInfo(window, IsWindowVisible(window), className.ToString(), title.ToString(),
                Modal: owner == IntPtr.Zero || !IsWindowEnabled(owner)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// The title (or the class when it has none) of a dialog that process <paramref name="processId"/> is showing and
    /// that stops it (modal), or null. Standard dialogs, Office's own (NUIDialog: passwords, sign-in, activation) and the
    /// older Office ones. A progress window of a long export, which has an enabled owner, is not one.
    /// </summary>
    public static string? BlockingDialogOf(int processId) =>
        WindowsOf(processId)
            .Where(w => w.Visible && w.Modal && IsDialogClass(w.ClassName))
            .Select(w => w.Title.Length > 0 ? w.Title : w.ClassName)
            .FirstOrDefault();

    internal static bool IsDialogClass(string className) =>
        className == "#32770" || className.StartsWith("NUIDialog", StringComparison.Ordinal)
        || className.StartsWith("bosa_sdm_", StringComparison.Ordinal);

    // ───────────────────────── Command line of a process ─────────────────────────

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr buffer, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    /// <summary>The command line of process <paramref name="processId"/> (Windows 8.1 and later), or null.</summary>
    public static string? CommandLineOf(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out var needed);
                    if (status == 0)
                    {
                        // UNICODE_STRING: ushort Length, ushort MaximumLength, (padding), pointer to the characters.
                        var length = (ushort)Marshal.ReadInt16(buffer);
                        var characters = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        return characters == IntPtr.Zero ? "" : Marshal.PtrToStringUni(characters, length / 2);
                    }
                    if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall)
                        return null;
                    size = Math.Max(needed, size * 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // ───────────────────────── Job object ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    private static readonly object JobLock = new();
    private static IntPtr _job;

    /// <summary>
    /// Puts process <paramref name="processId"/> in a job that Windows closes, killing what is in it, when Mandarina
    /// ends (also if it crashes): an Office that Mandarina started never outlives it. False when Windows refuses.
    /// </summary>
    public static bool KillWithMandarina(int processId)
    {
        lock (JobLock)
        {
            if (_job == IntPtr.Zero)
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    return false;
                var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
                var size = Marshal.SizeOf<ExtendedLimitInformation>();
                var memory = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, memory, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, memory, (uint)size))
                    {
                        CloseHandle(job);
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }
                _job = job; // never closed on purpose: it closes with the process
            }

            var process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, processId);
            if (process == IntPtr.Zero)
                return false;
            try
            {
                return AssignProcessToJobObject(_job, process);
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }

    // ───────────────────────── Office licensing ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LicensingStatus
    {
        public Guid SkuId;
        public int Status; // 0 unlicensed, 1 licensed, 2 in grace period, 3 notification
        public uint GraceMinutes;
        public uint TotalGraceDays;
        public int Reason;
        public ulong ValidityExpiration;
    }

    [DllImport("slc.dll")]
    private static extern int SLOpen(out IntPtr handle);

    [DllImport("slc.dll")]
    private static extern int SLClose(IntPtr handle);

    [DllImport("slc.dll")]
    private static extern int SLGetLicensingStatusInformation(IntPtr handle, ref Guid application, IntPtr sku, IntPtr right, out uint count, out IntPtr status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Application id of Office in Windows' Software Protection Platform.</summary>
    private static readonly Guid OfficeApplicationId = new("0ff1ce15-a989-479d-af46-f275c6370663");

    /// <summary>
    /// What Windows' Software Protection Platform says about the Office licenses installed (the same data as OSPP.VBS
    /// /dstatus). SKUs without a license installed (status 0) don't count.
    /// </summary>
    /// <returns>The state, and a signature of the statuses that changes when Office gets activated.</returns>
    public static (OfficeLicense State, string Signature) QueryOfficeLicense()
    {
        try
        {
            if (SLOpen(out var handle) != 0)
                return (OfficeLicense.Unknown, "");
            try
            {
                var application = OfficeApplicationId;
                if (SLGetLicensingStatusInformation(handle, ref application, IntPtr.Zero, IntPtr.Zero, out var count, out var memory) != 0
                    || memory == IntPtr.Zero)
                    return (OfficeLicense.Unknown, "");
                try
                {
                    var size = Marshal.SizeOf<LicensingStatus>();
                    var installed = new List<LicensingStatus>();
                    for (var i = 0; i < count; i++)
                    {
                        var status = Marshal.PtrToStructure<LicensingStatus>(IntPtr.Add(memory, i * size));
                        if (status.Status != 0)
                            installed.Add(status);
                    }
                    var signature = string.Join(",", installed.OrderBy(s => s.SkuId).Select(s => $"{s.SkuId:N}:{s.Status}"));
                    if (installed.Any(s => s.Status is 1 or 2))
                        return (OfficeLicense.Licensed, signature);
                    return installed.Count > 0 ? (OfficeLicense.Restricted, signature) : (OfficeLicense.Unknown, signature);
                }
                finally
                {
                    LocalFree(memory);
                }
            }
            finally
            {
                SLClose(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return (OfficeLicense.Unknown, "");
        }
    }
}
