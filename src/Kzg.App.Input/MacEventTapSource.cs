using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kzg.App.Input;

/// <summary>
/// Keyboard and mouse buttons through a listen-only Quartz event tap (<c>kCGEventTapOptionListenOnly</c>).
/// Unlike libuiohook's active tap, macOS never waits for the app's answer, so a busy app can't lag the
/// computer, and a tap that macOS does disable (timeout or user input) is switched on again, here and from
/// the watchdog. It needs Input Monitoring and not Accessibility. The callback only enqueues; a consumer
/// thread raises <see cref="Input"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacEventTapSource : IKeyMouseSource
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint SessionEventTap = 1;
    private const uint HeadInsertEventTap = 0;
    private const uint ListenOnly = 1;
    private const int KeyboardKeycodeField = 9;
    private const int MouseButtonNumberField = 3;

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

    /// <summary>Switches a tap macOS disabled back on, and says whether it now is.</summary>
    public bool TryRecover()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
        }
        return session?.ReEnable() == true;
    }

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
        private readonly MacEventTapSource _owner;
        private readonly Channel<RawInput> _queue = Channel.CreateUnbounded<RawInput>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly ManualResetEventSlim _started = new();
        private GCHandle _self;
        private nint _tap;
        private nint _runLoop;
        private Thread? _thread;

        public Session(MacEventTapSource owner) => _owner = owner;

        public void Run()
        {
            _self = GCHandle.Alloc(this);
            _ = Task.Run(async () =>
            {
                await foreach (var raw in _queue.Reader.ReadAllAsync())
                {
                    _owner.Input?.Invoke(raw);
                }
            });
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "Kzg key tap" };
            _thread.Start();
            // Whether the tap could be made is known within moments; the owner is told from the thread.
            _ = _started.Wait(TimeSpan.FromSeconds(5));
        }

        public bool ReEnable()
        {
            if (_tap == 0)
            {
                return false;
            }
            CGEventTapEnable(_tap, true);
            return CGEventTapIsEnabled(_tap);
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            if (_runLoop != 0)
            {
                CFRunLoopStop(_runLoop);
            }
            _ = _thread?.Join(TimeSpan.FromSeconds(2));
            if (_self.IsAllocated)
            {
                _self.Free();
            }
            _started.Dispose();
        }

        private unsafe void RunLoop()
        {
            var info = GCHandle.ToIntPtr(_self);
            _tap = CGEventTapCreate(SessionEventTap, HeadInsertEventTap, ListenOnly, MacEventDecoder.EventMask, (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint>)&OnEvent, info);
            if (_tap == 0)
            {
                // No Input Monitoring yet: macOS refuses a listen-only tap, and shows its own prompt the first time.
                if (_owner.IsCurrent(this))
                {
                    _owner.SetHealth(HookHealth.NeedsAccessibility, null);
                }
                _started.Set();
                return;
            }
            var source = CFMachPortCreateRunLoopSource(0, _tap, 0);
            _runLoop = CFRunLoopGetCurrent();
            CFRunLoopAddSource(_runLoop, source, CommonModes());
            CGEventTapEnable(_tap, true);
            if (_owner.IsCurrent(this))
            {
                _owner.SetHealth(HookHealth.Ok, null);
            }
            _started.Set();
            CFRunLoopRun();
            CGEventTapEnable(_tap, false);
            CFRunLoopRemoveSource(_runLoop, source, CommonModes());
            CFMachPortInvalidate(_tap);
            CFRelease(source);
            CFRelease(_tap);
            _tap = 0;
        }

        private void Enqueue(uint type, nint cgEvent)
        {
            if (type is MacEventDecoder.TapDisabledByTimeout or MacEventDecoder.TapDisabledByUserInput)
            {
                // macOS switched the tap off; put it back before anything else is missed.
                CGEventTapEnable(_tap, true);
                return;
            }
            var key = CGEventGetIntegerValueField(cgEvent, KeyboardKeycodeField);
            var button = CGEventGetIntegerValueField(cgEvent, MouseButtonNumberField);
            if (MacEventDecoder.Decode(type, key, button, CGEventGetFlags(cgEvent)) is { } raw)
            {
                _queue.Writer.TryWrite(raw);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nint OnEvent(nint proxy, uint type, nint cgEvent, nint info)
        {
            if (GCHandle.FromIntPtr(info).Target is Session session)
            {
                session.Enqueue(type, cgEvent);
            }
            return cgEvent;
        }

        private static nint CommonModes() => Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes"));
    }

    [LibraryImport(CoreGraphics)]
    private static partial nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, nint callback, nint userInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGEventTapIsEnabled(nint tap);

    [LibraryImport(CoreGraphics)]
    private static partial long CGEventGetIntegerValueField(nint cgEvent, int field);

    [LibraryImport(CoreGraphics)]
    private static partial ulong CGEventGetFlags(nint cgEvent);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopRemoveSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopRun();

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopStop(nint loop);

    [LibraryImport(CoreFoundation)]
    private static partial void CFMachPortInvalidate(nint port);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
