using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpHook.Data;

namespace Kzg.App.Input;

/// <summary>
/// The character the current keyboard layout types on a key, with no modifiers, so a binding reads "-" on the
/// key that types "-" and not "Slash" for its US position. Null when the OS can't say; the caller then uses a
/// built-in symbol. On macOS the Text Input Source calls must run on the main thread: the only callers are
/// <c>MainViewModel</c>'s getters and key capture, which run on the UI thread, and nothing else may call this.
/// </summary>
public static partial class SystemKeyLabels
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kUCKeyActionDisplay, and kUCKeyTranslateNoDeadKeysMask so a dead key shows its own character.
    private const ushort KeyActionDisplay = 3;
    private const uint NoDeadKeys = 1;
    private const uint MapVkToChar = 2;
    private const uint DeadKeyBit = 0x80000000;

    private static readonly Lazy<Dictionary<KeyCode, int>> MacCodes = new(() => Reverse(0x00, 0x7F, code => MacEventDecoder.KeyOf(code)));

    // One VK can be two keys (plain and E0-extended), so both are scanned; the character keys have no twin.
    private static readonly Lazy<Dictionary<KeyCode, int>> WindowsCodes = new(() =>
    {
        var map = Reverse(0x00, 0xFF, code => RawInputDecoder.KeyOf((ushort)code, 0, false));
        foreach (var (key, code) in Reverse(0x00, 0xFF, code => RawInputDecoder.KeyOf((ushort)code, 0, true)))
        {
            map.TryAdd(key, code);
        }
        return map;
    });

    internal static IReadOnlyDictionary<KeyCode, int> MacVirtualKeys => MacCodes.Value;

    internal static IReadOnlyDictionary<KeyCode, int> WindowsVirtualKeys => WindowsCodes.Value;

    public static string? CharacterOf(KeyCode key)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacCodes.Value.TryGetValue(key, out var code) ? MacCharacter((ushort)code) : null;
            }
            if (OperatingSystem.IsWindows())
            {
                return WindowsCodes.Value.TryGetValue(key, out var vk) ? WindowsCharacter((uint)vk) : null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        {
            // A missing native symbol means "can't say", and the built-in symbol is shown.
        }
        return null;
    }

    private static Dictionary<KeyCode, int> Reverse(int from, int to, Func<int, KeyCode?> decode)
    {
        var map = new Dictionary<KeyCode, int>();
        for (var code = from; code <= to; code++)
        {
            if (decode(code) is { } key)
            {
                map.TryAdd(key, code);
            }
        }
        return map;
    }

    [SupportedOSPlatform("macos")]
    private static string? MacCharacter(ushort keyCode)
    {
        var source = TISCopyCurrentKeyboardLayoutInputSource();
        if (source == 0)
        {
            return null;
        }
        try
        {
            var property = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Carbon), "kTISPropertyUnicodeKeyLayoutData"));
            var data = TISGetInputSourceProperty(source, property);
            if (data == 0)
            {
                return null;
            }
            var layout = CFDataGetBytePtr(data);
            uint deadKeys = 0;
            Span<char> buffer = stackalloc char[8];
            nuint length;
            int status;
            unsafe
            {
                fixed (char* text = buffer)
                {
                    status = UCKeyTranslate(layout, keyCode, KeyActionDisplay, 0, LMGetKbdType(), NoDeadKeys, &deadKeys, (nuint)buffer.Length, &length, text);
                }
            }
            return status == 0 && length > 0 ? new string(buffer[..(int)length]) : null;
        }
        finally
        {
            CFRelease(source);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsCharacter(uint virtualKey)
    {
        var result = MapVirtualKeyExW(virtualKey, MapVkToChar, GetKeyboardLayout(0));
        return result == 0 || (result & DeadKeyBit) != 0 ? null : ((char)(result & 0xFFFF)).ToString();
    }

    [LibraryImport(Carbon)]
    private static partial nint TISCopyCurrentKeyboardLayoutInputSource();

    [LibraryImport(Carbon)]
    private static partial nint TISGetInputSourceProperty(nint source, nint key);

    [LibraryImport(Carbon)]
    private static partial byte LMGetKbdType();

    [LibraryImport(Carbon)]
    private static unsafe partial int UCKeyTranslate(nint layout, ushort virtualKeyCode, ushort action, uint modifiers, uint keyboardType, uint options, uint* deadKeyState, nuint maxLength, nuint* actualLength, char* text);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint thread);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);
}
