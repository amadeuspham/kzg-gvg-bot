using System.Text;
using SDL;

namespace Kzg.App.Input;

/// <summary>
/// The controller half, run by a child copy of the app's own exe started with <see cref="Argument"/>.
/// It is the only code that calls <c>SDL_Init</c>, and it never loads the keyboard hook: on macOS an
/// SDL-initialised process stops delivering hook events. It writes <see cref="PadLine"/>s to stdout and
/// ends when its stdin closes, so it cannot outlive the app.
/// </summary>
public static unsafe class PadHelper
{
    public const string Argument = "--pad-helper";

    public static bool IsHelperInvocation(IReadOnlyList<string> args) => args.Contains(Argument);

    /// <summary>Runs on the calling thread, which SDL wants to be the process's main thread. Returns the exit code.</summary>
    public static int Run()
    {
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var cancel = new CancellationTokenSource();
        WatchParent(cancel);

        // Without this SDL drops controller events whenever no SDL window has focus, and we have none.
        SDL3.SDL_SetHint(SDL3.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD | SDL_InitFlags.SDL_INIT_EVENTS))
        {
            output.WriteLine(PadLine.Format(new PadLine.Failure("SDL could not start: " + SDL3.SDL_GetError())));
            return 1;
        }
        output.WriteLine(PadLine.Format(new PadLine.Ready(SDL3.SDL_GetVersion().ToString())));

        var open = new Dictionary<SDL_JoystickID, (nint Handle, string Name)>();
        var count = 0;
        var ids = SDL3.SDL_GetGamepads(&count);
        for (var i = 0; i < count; i++)
        {
            Open(ids[i], open, output);
        }
        SDL3.SDL_free(ids);

        SDL_Event e;
        while (!cancel.IsCancellationRequested)
        {
            // A short wait keeps the loop answering to a closed stdin without spinning.
            if (!SDL3.SDL_WaitEventTimeout(&e, 100))
            {
                continue;
            }
            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    Open(e.gdevice.which, open, output);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    if (open.Remove(e.gdevice.which, out var gone))
                    {
                        SDL3.SDL_CloseGamepad((SDL_Gamepad*)gone.Handle);
                        output.WriteLine(PadLine.Format(new PadLine.ControllerRemoved(gone.Name)));
                    }
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                    var name = SDL3.SDL_GetGamepadStringForButton((SDL_GamepadButton)e.gbutton.button);
                    if (!string.IsNullOrEmpty(name))
                    {
                        output.WriteLine(PadLine.Format(new PadLine.Button(name, e.Type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN)));
                    }
                    break;
            }
        }
        foreach (var (handle, _) in open.Values)
        {
            SDL3.SDL_CloseGamepad((SDL_Gamepad*)handle);
        }
        SDL3.SDL_Quit();
        return 0;
    }

    private static void Open(SDL_JoystickID id, Dictionary<SDL_JoystickID, (nint Handle, string Name)> open, StreamWriter output)
    {
        // Pads present at start-up arrive again as ADDED events.
        if (open.ContainsKey(id))
        {
            return;
        }
        var gamepad = SDL3.SDL_OpenGamepad(id);
        if (gamepad == null)
        {
            return;
        }
        var name = SDL3.SDL_GetGamepadName(gamepad) ?? "controller";
        open[id] = ((nint)gamepad, name);
        output.WriteLine(PadLine.Format(new PadLine.ControllerAdded(name)));
    }

    private static void WatchParent(CancellationTokenSource cancel) =>
        new Thread(() =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                var buffer = new byte[64];
                while (input.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
            catch (IOException)
            {
                // The pipe is gone: the parent is too.
            }
            cancel.Cancel();
        })
        { IsBackground = true, Name = "parent-watch" }.Start();
}
