using System.Runtime.InteropServices;

namespace FastTorrentDownload.Services;

/// <summary>
/// Keeps Windows from sleeping while transfers run. Uses a power request handle, which is
/// process-wide; SetThreadExecutionState only applies to the calling thread, so an
/// assertion made from one awaiter could never be cleared from another.
/// </summary>
public sealed class SystemSleepGuard : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly object _sync = new();
    private IntPtr _request;
    private IntPtr _reason;
    private bool _active;
    private bool _disposed;

    public void Update(bool hasActiveTransfers)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed || _active == hasActiveTransfers)
            {
                return;
            }

            try
            {
                if (_request == IntPtr.Zero)
                {
                    _reason = Marshal.StringToHGlobalUni("Active torrent transfers");
                    var context = new ReasonContext
                    {
                        Version = PowerRequestContextVersion,
                        Flags = PowerRequestContextSimpleString,
                        SimpleReasonString = _reason
                    };
                    _request = PowerCreateRequest(ref context);
                    if (_request == IntPtr.Zero || _request == new IntPtr(-1))
                    {
                        _request = IntPtr.Zero;
                        return;
                    }
                }

                _active = hasActiveTransfers
                    ? PowerSetRequest(_request, PowerRequestSystemRequired)
                    : !PowerClearRequest(_request, PowerRequestSystemRequired);
            }
            catch
            {
                // Non-critical power hint.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_request != IntPtr.Zero)
                {
                    if (_active) PowerClearRequest(_request, PowerRequestSystemRequired);
                    CloseHandle(_request);
                    _request = IntPtr.Zero;
                }
            }
            catch
            {
                // Non-critical power hint.
            }
            finally
            {
                if (_reason != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_reason);
                    _reason = IntPtr.Zero;
                }
            }
        }
    }
}
