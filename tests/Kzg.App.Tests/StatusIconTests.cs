using Kzg.App.Services;

namespace Kzg.App.Tests;

public class StatusIconTests
{
    private static readonly TrayState[] States = Enum.GetValues<TrayState>();

    [Fact]
    public void Every_state_has_its_own_color()
    {
        Assert.Equal(States.Length, States.Select(StatusIcon.ColorOf).Distinct().Count());
    }

    [Fact]
    public void Every_state_has_its_own_silhouette_so_color_is_never_the_only_signal()
    {
        var shapes = States.Select(s => string.Concat(Enumerable.Range(0, 16).SelectMany(
            y => Enumerable.Range(0, 16).Select(x => StatusIcon.Inside(s, (x + 0.5) / 16, (y + 0.5) / 16) ? '#' : '.')))).ToList();

        Assert.Equal(States.Length, shapes.Distinct().Count());
        Assert.All(shapes, shape => Assert.Contains('#', shape));
    }

    [Fact]
    public void The_rendered_icon_is_premultiplied_in_the_state_color_and_clear_at_the_corners()
    {
        var pixels = StatusIcon.RenderBgra(TrayState.Connected, 32);
        var (r, g, b) = StatusIcon.ColorOf(TrayState.Connected);

        var center = ((16 * 32) + 16) * 4;
        Assert.Equal([b, g, r, 255], pixels[center..(center + 4)]);
        Assert.Equal([0, 0, 0, 0], pixels[..4]);
        Assert.Equal(32 * 32 * 4, pixels.Length);
    }

    [Fact]
    public void Open_mic_is_a_microphone_a_capsule_over_a_stand_open_between_them()
    {
        // The capsule's middle, the stand's foot, and the gap between capsule and holder.
        Assert.True(StatusIcon.Inside(TrayState.OpenMic, 0.5, 0.3));
        Assert.True(StatusIcon.Inside(TrayState.OpenMic, 0.5, 0.94));
        Assert.False(StatusIcon.Inside(TrayState.OpenMic, 0.27, 0.3));
        Assert.False(StatusIcon.Inside(TrayState.OpenMic, 0.5, 0.7));
        Assert.True(StatusIcon.Inside(TrayState.OpenMic, 0.2, 0.45));
    }
}
