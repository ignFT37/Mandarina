// Mandarina: an Office object driven by late binding (IDispatch), found by ProgID ("Word.Application"). No interop
// assemblies and no `dynamic`: names are looked up when called, so any Office that has the object model works, and a member
// that doesn't exist in this version is an error of that call, not of the build. The wrapper releases the COM object when it
// is disposed (Office only quits when nothing holds a reference), and retries a call Office rejects because it is busy.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Engines.Office.Com;

/// <summary>A COM call that failed, with the HRESULT Office answered.</summary>
internal sealed class OfficeComException(string member, int hresult, string message, Exception? inner = null)
    : Exception($"{member}: {message} (0x{hresult:X8})", inner)
{
    /// <summary>The call that failed ("Documents.Open" is just "Open").</summary>
    public string Member { get; } = member;

    public int Code { get; } = hresult;

    /// <summary>RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER: Office is busy (a window of its own is open).</summary>
    public bool IsBusy => Code is unchecked((int)0x80010001) or unchecked((int)0x8001010A);

    /// <summary>The Office process is gone (closed, killed, crashed): the object can't be used any more.</summary>
    public bool IsServerGone => Code is unchecked((int)0x800706BA) or unchecked((int)0x800706BE) or unchecked((int)0x800706BF)
        or unchecked((int)0x80010108) or unchecked((int)0x80010012) or unchecked((int)0x80010007) or unchecked((int)0x800401FD);
}

[SupportedOSPlatform("windows")]
internal sealed class ComObject : IDisposable
{
    /// <summary>How many times, and how far apart, a call that Office rejects as busy is repeated.</summary>
    private const int BusyRetries = 40;
    private static readonly TimeSpan BusyPause = TimeSpan.FromMilliseconds(250);

    private object? _raw;

    private ComObject(object raw) => _raw = raw;

    /// <summary>Starts (or attaches to) the application registered as <paramref name="progId"/>.</summary>
    public static ComObject Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return new ComObject(Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{progId} could not be created."));
    }

    private object Raw => _raw ?? throw new ObjectDisposedException(nameof(ComObject));

    /// <summary>A property that is an object (Documents, ActiveWindow, ...).</summary>
    public ComObject Child(string name, params object?[] args) =>
        new(Invoke(name, BindingFlags.GetProperty, args, null) ?? throw new OfficeComException(name, unchecked((int)0x80004003), "no object"));

    /// <summary>A property value (a number, text, true / false).</summary>
    public object? Get(string name, params object?[] args) => Invoke(name, BindingFlags.GetProperty, args, null);

    public void Set(string name, object? value) => Invoke(name, BindingFlags.SetProperty, [value], null);

    /// <summary>A method, arguments by position.</summary>
    public object? Call(string name, params object?[] args) => Invoke(name, BindingFlags.InvokeMethod, args, null);

    /// <summary>A method that returns an object (Dialogs.Item, ...), arguments by position.</summary>
    public ComObject CallChild(string name, params object?[] args) =>
        new(Call(name, args) ?? throw new OfficeComException(name, unchecked((int)0x80004003), "returned nothing"));

    /// <summary>A method, arguments by name (the ones left out keep Office's defaults).</summary>
    public object? CallNamed(string name, params (string Name, object? Value)[] args) =>
        Invoke(name, BindingFlags.InvokeMethod, [.. args.Select(a => a.Value)], [.. args.Select(a => a.Name)]);

    /// <summary>A method that returns an object (Documents.Open, Workbooks.Open, ...), arguments by name.</summary>
    public ComObject CallObject(string name, params (string Name, object? Value)[] args) =>
        new(CallNamed(name, args) ?? throw new OfficeComException(name, unchecked((int)0x80004003), "returned nothing"));

    private object? Invoke(string name, BindingFlags flags, object?[] args, string[]? names)
    {
        var values = args.Select(a => a is ComObject c ? c.Raw : a).ToArray();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Raw.GetType().InvokeMember(name, flags, null, Raw, values, null, null, names);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException com)
            {
                var failure = new OfficeComException(name, com.HResult, com.Message, com);
                if (failure.IsBusy && attempt < BusyRetries)
                {
                    Thread.Sleep(BusyPause);
                    continue;
                }
                throw failure;
            }
            catch (COMException com)
            {
                throw new OfficeComException(name, com.HResult, com.Message, com);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new OfficeComException(name, ex.InnerException.HResult, ex.InnerException.Message, ex.InnerException);
            }
        }
    }

    /// <summary>Releases the COM object (the Office process quits once nobody holds it and it has no window).</summary>
    public void Dispose()
    {
        var raw = Interlocked.Exchange(ref _raw, null);
        if (raw is not null && Marshal.IsComObject(raw))
        {
            try
            {
                Marshal.FinalReleaseComObject(raw);
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                // the server is already gone
            }
        }
    }
}
