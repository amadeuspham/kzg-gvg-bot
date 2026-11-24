using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kzg.App.Input;

/// <summary>
/// Keyboard and mouse buttons through Windows Raw Input (<c>RIDEV_INPUTSINK</c>) on a hidden message window.
/// Windows delivers the records into a queue and never waits for the app, so a slow app can't lag the
/// computer, and nothing is installed that Windows can remove after a timeout, as it does a low-level hook.
/// The message thread only enqueues; a consumer thread raises <see cref="Input"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRawInputSource : IKeyMouseSource
{
    private const uint InputSink = 0x00000100;
    private const uint Remove = 0x00000001;
    private const uint WmInput = 0x00FF;
    private const uint WmQuit = 0x0012;
    private const uint RidInput = 0x10000003;
    private static readonly nint MessageOnlyParent = -3;

    private readonly object _gate = new();
    private Session? _session;

    public event Action<RawInput>? Input;

    public event Action<HookHealth, string?>? HealthChanged;

    public HookHealth Health { get; private set; } = HookHealth.Stopped;

    public string? Detail { get; private set; }

    public void Start()
    {
        Session session;
        lock (_gate)
        {
            if (_session is not null)
            {
                return;
            }
            _session = session = new Session(this);
        }
        SetHealth(HookHealth.Starting, null);
        session.Run();
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is null)
        {
            return;
        }
        session.Dispose();
        SetHealth(HookHealth.Stopped, null);
    }

    public void Dispose() => Stop();

    public void MarkFailed(string detail)
    {
        if (Health == HookHealth.Ok)
        {
            SetHealth(HookHealth.Failed, detail);
        }
    }

    /// <summary>Nothing outside can switch a stopped message loop back on; the owner restarts the source.</summary>
    public bool TryRecover() => false;

    public void Maintain()
    {
    }

    private void SetHealth(HookHealth health, string? detail)
    {
        Health = health;
        Detail = detail;
        HealthChanged?.Invoke(health, detail);
    }

    private bool IsCurrent(Session session)
    {
        lock (_gate)
        {
            return ReferenceEquals(_session, session);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly WindowsRawInputSource _owner;
        private readonly Channel<RawInput> _queue = Channel.CreateUnbounded<RawInput>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly ManualResetEventSlim _started = new();
        private Thread? _thread;
        private volatile uint _threadId;
        private volatile bool _stopping;

        public Session(WindowsRawInputSource owner) => _owner = owner;

        public void Run()
        {
            _ = Task.Run(async () =>
            {
                await foreach (var raw in _queue.Reader.ReadAllAsync())
                {
                    _owner.Input?.Invoke(raw);
                }
            });
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Kzg raw input" };
            _thread.Start();
            _ = _started.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            _stopping = true;
            _queue.Writer.TryComplete();
            if (_threadId != 0)
            {
                _ = PostThreadMessageW(_threadId, WmQuit, 0, 0);
            }
            _ = _thread?.Join(TimeSpan.FromSeconds(2));
            _started.Dispose();
        }

        private void MessageLoop()
        {
            _threadId = GetCurrentThreadId();
            var window = CreateWindowExW(0, "STATIC", string.Empty, 0, 0, 0, 0, 0, MessageOnlyParent, 0, 0, 0);
            if (window == 0)
            {
                Fail($"Windows would not make the raw input window ({Marshal.GetLastPInvokeError()}).");
                return;
            }
            var devices = new[]
            {
                new RawInputDevice { UsagePage = 1, Usage = 6, Flags = InputSink, Target = window },
                new RawInputDevice { UsagePage = 1, Usage = 2, Flags = InputSink, Target = window },
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                var code = Marshal.GetLastPInvokeError();
                _ = DestroyWindow(window);
                Fail($"Windows refused raw input ({code}).");
                return;
            }
            if (_owner.IsCurrent(this))
            {
                _owner.SetHealth(HookHealth.Ok, null);
            }
            _started.Set();
            Pump();
            for (var i = 0; i < devices.Length; i++)
            {
                devices[i].Flags = Remove;
                devices[i].Target = 0;
            }
            _ = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
            _ = DestroyWindow(window);
            if (!_stopping && _owner.IsCurrent(this))
            {
                _owner.SetHealth(HookHealth.Failed, "The raw input listener stopped.");
            }
        }

        private void Pump()
        {
            var headerSize = Marshal.SizeOf<RawInputHeader>();
            Span<byte> buffer = stackalloc byte[128];
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == WmInput)
                {
                    var size = (uint)buffer.Length;
                    var copied = GetRawInputData(message.LParam, RidInput, ref MemoryMarshal.GetReference(buffer), ref size, (uint)headerSize);
                    if (copied != unchecked((uint)-1))
                    {
                        foreach (var raw in RawInputDecoder.Decode(buffer[..(int)copied], headerSize))
                        {
                            _queue.Writer.TryWrite(raw);
                        }
                    }
                }
                // The system's own window procedure frees the record behind a WM_INPUT.
                _ = DispatchMessageW(ref message);
            }
        }

        private void Fail(string detail)
        {
            if (_owner.IsCurrent(this))
            {
                _owner.SetHealth(HookHealth.Failed, detail);
            }
            _started.Set();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(nint rawInput, uint command, ref byte data, ref uint size, uint headerSize);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessageW(out Message message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessageW(ref Message message);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
