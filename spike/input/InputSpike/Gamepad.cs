using SDL;

namespace InputSpike;

/// <summary>
/// SDL3 gamepad listener. SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS must be set before SDL_Init,
/// otherwise SDL drops joystick events while our window is not focused (we have no window at all).
/// </summary>
static unsafe class Gamepad
{
    public static void Run(CancellationToken ct)
    {
        if (!SDL3.SDL_SetHint(SDL3.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1"))
            Log.Write("pad", "WARN: SDL_SetHint(ALLOW_BACKGROUND_EVENTS) failed");
        if (!SDL3.SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD | SDL_InitFlags.SDL_INIT_EVENTS))
        {
            Log.Write("pad", $"SDL_Init failed: {SDL3.SDL_GetError()}");
            return;
        }
        Log.Write("pad", $"SDL {SDL3.SDL_GetVersion()} initialised, background events hint = {SDL3.SDL_GetHint(SDL3.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS)}");

        var open = new Dictionary<SDL_JoystickID, nint>();
        int count;
        var ids = SDL3.SDL_GetGamepads(&count);
        if (count == 0) Log.Write("pad", "NO CONTROLLER ATTACHED. Plug one in: hot-plug is handled, buttons will log below.");
        for (var i = 0; i < count; i++) Open(ids[i], open);
        SDL3.SDL_free(ids);

        SDL_Event e;
        while (!ct.IsCancellationRequested)
        {
            // Short wait keeps the loop responsive to cancellation without busy-spinning.
            if (!SDL3.SDL_WaitEventTimeout(&e, 100)) continue;
            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    Open(e.gdevice.which, open);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    if (open.Remove(e.gdevice.which, out var h)) SDL3.SDL_CloseGamepad((SDL_Gamepad*)h);
                    Log.Write("pad", $"controller removed (id {e.gdevice.which})");
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                    var down = e.Type == SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN;
                    Log.Write("pad", $"{SDL3.SDL_GetGamepadStringForButton((SDL_GamepadButton)e.gbutton.button)} {(down ? "DOWN" : "UP")}  (id {e.gbutton.which})");
                    break;
            }
        }
        foreach (var h in open.Values) SDL3.SDL_CloseGamepad((SDL_Gamepad*)h);
        SDL3.SDL_Quit();
    }

    static void Open(SDL_JoystickID id, Dictionary<SDL_JoystickID, nint> open)
    {
        if (open.ContainsKey(id)) return;   // pads present at init also arrive as GAMEPAD_ADDED
        var g = SDL3.SDL_OpenGamepad(id);
        if (g == null) { Log.Write("pad", $"SDL_OpenGamepad({id}) failed: {SDL3.SDL_GetError()}"); return; }
        open[id] = (nint)g;
        Log.Write("pad", $"controller opened: \"{SDL3.SDL_GetGamepadName(g)}\" (id {id})");
    }
}
