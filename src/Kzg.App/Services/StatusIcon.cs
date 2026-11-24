namespace Kzg.App.Services;

/// <summary>What the tray icon shows: the badge's state, plus open mic, which is live without a key held.</summary>
public enum TrayState
{
    Connected,
    Reconnecting,
    Broadcasting,
    OpenMic,
    Disconnected,
}

/// <summary>
/// The tray and menu-bar icon for each tray state. Color is never the only signal: every state has its
/// own silhouette too (circle, diamond, triangle, microphone, square), matching the badge in the window.
/// </summary>
public static class StatusIcon
{
    // Half the width of the microphone's strokes, in icon units.
    private const double Stroke = 0.045;

    public static (byte R, byte G, byte B) ColorOf(TrayState state) => state switch
    {
        TrayState.Connected => (0x1F, 0x7A, 0x45),
        TrayState.Reconnecting => (0xE0, 0xA1, 0x00),
        TrayState.Broadcasting => (0x1F, 0x5F, 0xBF),
        TrayState.OpenMic => (0xE0, 0x35, 0x2B),
        _ => (0xB0, 0x2A, 0x2A),
    };

    /// <summary>Premultiplied BGRA pixels, row by row, <paramref name="size"/> squared.</summary>
    public static byte[] RenderBgra(TrayState state, int size)
    {
        var (r, g, b) = ColorOf(state);
        var pixels = new byte[size * size * 4];
        const int Samples = 4;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var covered = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var u = (x + ((sx + 0.5) / Samples)) / size;
                        var v = (y + ((sy + 0.5) / Samples)) / size;
                        covered += Inside(state, u, v) ? 1 : 0;
                    }
                }
                var alpha = covered * 255 / (Samples * Samples);
                var at = ((y * size) + x) * 4;
                pixels[at] = (byte)(b * alpha / 255);
                pixels[at + 1] = (byte)(g * alpha / 255);
                pixels[at + 2] = (byte)(r * alpha / 255);
                pixels[at + 3] = (byte)alpha;
            }
        }
        return pixels;
    }

    /// <summary>Whether the point (u, v), each from 0 to 1, lies inside the state's silhouette.</summary>
    public static bool Inside(TrayState state, double u, double v)
    {
        double dx = u - 0.5, dy = v - 0.5;
        return state switch
        {
            TrayState.Connected => (dx * dx) + (dy * dy) <= 0.45 * 0.45,
            TrayState.Reconnecting => Math.Abs(dx) + Math.Abs(dy) <= 0.48,
            TrayState.Broadcasting => v is >= 0.08 and <= 0.92 && Math.Abs(dx) <= (v - 0.08) * 0.5952,
            TrayState.OpenMic => InMicrophone(u, v),
            _ => Math.Abs(dx) <= 0.40 && Math.Abs(dy) <= 0.40,
        };
    }

    /// <summary>A capsule over a U-shaped holder, a stem and a base, as in the design's tray mockup.</summary>
    private static bool InMicrophone(double u, double v)
    {
        // The capsule: a 0.38-wide pill from 0.04 to 0.60.
        if (Segment(u, v, 0.5, 0.23, 0.5, 0.41) <= 0.19)
        {
            return true;
        }
        // The holder: down both sides to 0.48, then a half circle of radius 0.3 under the capsule.
        var below = v >= 0.48 && Math.Abs(Math.Sqrt(((u - 0.5) * (u - 0.5)) + ((v - 0.48) * (v - 0.48))) - 0.3) <= Stroke;
        return below
            || Segment(u, v, 0.2, 0.42, 0.2, 0.48) <= Stroke
            || Segment(u, v, 0.8, 0.42, 0.8, 0.48) <= Stroke
            || Segment(u, v, 0.5, 0.78, 0.5, 0.92) <= Stroke
            || Segment(u, v, 0.32, 0.94, 0.68, 0.94) <= Stroke;
    }

    /// <summary>The distance from (u, v) to the segment from (x1, y1) to (x2, y2).</summary>
    private static double Segment(double u, double v, double x1, double y1, double x2, double y2)
    {
        double sx = x2 - x1, sy = y2 - y1;
        var t = Math.Clamp((((u - x1) * sx) + ((v - y1) * sy)) / ((sx * sx) + (sy * sy)), 0, 1);
        double px = x1 + (t * sx) - u, py = y1 + (t * sy) - v;
        return Math.Sqrt((px * px) + (py * py));
    }
}
