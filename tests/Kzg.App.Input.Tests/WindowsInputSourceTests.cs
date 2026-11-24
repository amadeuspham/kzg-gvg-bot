using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class WindowsInputSourceTests
{
    private static readonly RawInput F8Down = new(InputBinding.Key(KeyCode.VcF8), true);

    private readonly List<FakeKeyMouseSource> _raws = [];
    private readonly List<FakeKeyMouseSource> _hooks = [];
    private readonly Queue<FakeKeyMouseSource> _rawQueue = new();
    private readonly Queue<FakeKeyMouseSource> _hookQueue = new();
    private readonly List<RawInput> _heard = [];
    private readonly WindowsInputSource _source;
    private HookHealth _rawDefault = HookHealth.Ok;

    public WindowsInputSourceTests()
    {
        _source = new WindowsInputSource(() => Next(_rawQueue, _raws), () => Next(_hookQueue, _hooks));
        _source.Input += _heard.Add;
    }

    private FakeKeyMouseSource Next(Queue<FakeKeyMouseSource> queue, List<FakeKeyMouseSource> made)
    {
        var source = queue.Count > 0 ? queue.Dequeue() : new FakeKeyMouseSource { StartsAs = ReferenceEquals(queue, _rawQueue) ? _rawDefault : HookHealth.Ok };
        made.Add(source);
        return source;
    }

    private FakeKeyMouseSource RawThatStartsAs(HookHealth health)
    {
        var raw = new FakeKeyMouseSource { StartsAs = health };
        _rawQueue.Enqueue(raw);
        return raw;
    }

    private FakeKeyMouseSource HookThatStartsAs(HookHealth health)
    {
        var hook = new FakeKeyMouseSource { StartsAs = health };
        _hookQueue.Enqueue(hook);
        return hook;
    }

    [Fact]
    public void Raw_input_that_works_is_all_there_is_and_no_hook_is_ever_installed()
    {
        var raw = RawThatStartsAs(HookHealth.Ok);

        _source.Start();
        raw.Raise(F8Down);
        _source.Maintain();

        Assert.Equal(HookHealth.Ok, _source.Health);
        Assert.False(_source.UsingFallback);
        Assert.Empty(_hooks);
        Assert.Equal([F8Down], _heard);
    }

    [Fact]
    public void When_raw_input_cannot_start_the_hook_takes_over_and_its_input_is_heard()
    {
        RawThatStartsAs(HookHealth.Failed);
        var hook = HookThatStartsAs(HookHealth.Ok);

        _source.Start();
        hook.Raise(F8Down);

        Assert.True(_source.UsingFallback);
        Assert.Equal(HookHealth.Ok, _source.Health);
        Assert.Equal(1, hook.Starts);
        Assert.Equal([F8Down], _heard);
    }

    [Fact]
    public void Raw_input_that_dies_later_hands_over_to_the_hook()
    {
        var raw = RawThatStartsAs(HookHealth.Ok);
        var hook = HookThatStartsAs(HookHealth.Ok);
        _source.Start();
        Assert.False(_source.UsingFallback);

        raw.Set(HookHealth.Failed, "The raw input listener stopped.");

        Assert.True(_source.UsingFallback);
        Assert.Equal(1, hook.Starts);
        Assert.Equal(HookHealth.Ok, _source.Health);
    }

    [Fact]
    public void Each_maintenance_puts_a_new_hook_in_beside_the_old_one_and_drops_the_old_one_once_the_new_one_works()
    {
        _rawDefault = HookHealth.Failed;
        RawThatStartsAs(HookHealth.Failed);
        var old = HookThatStartsAs(HookHealth.Ok);
        var fresh = HookThatStartsAs(HookHealth.Starting);
        _source.Start();

        _source.Maintain();

        Assert.False(old.Disposed);
        old.Raise(F8Down);
        fresh.Raise(F8Down);
        Assert.Equal([F8Down], _heard);

        fresh.Set(HookHealth.Ok);

        Assert.True(old.Disposed);
        fresh.Raise(F8Down);
        old.Raise(F8Down);
        Assert.Equal([F8Down, F8Down], _heard);
        Assert.Equal(HookHealth.Ok, _source.Health);
    }

    [Fact]
    public void A_replacement_hook_that_fails_is_dropped_and_the_old_one_stays()
    {
        _rawDefault = HookHealth.Failed;
        RawThatStartsAs(HookHealth.Failed);
        var old = HookThatStartsAs(HookHealth.Ok);
        var fresh = HookThatStartsAs(HookHealth.Failed);
        _source.Start();

        _source.Maintain();

        Assert.True(fresh.Disposed);
        Assert.False(old.Disposed);
        Assert.Equal(HookHealth.Ok, _source.Health);
    }

    [Fact]
    public void A_hook_that_died_is_reported_and_the_next_maintenance_replaces_it()
    {
        _rawDefault = HookHealth.Failed;
        RawThatStartsAs(HookHealth.Failed);
        var old = HookThatStartsAs(HookHealth.Ok);
        var fresh = HookThatStartsAs(HookHealth.Ok);
        _source.Start();

        old.Set(HookHealth.Failed, "The input hook stopped.");
        Assert.Equal(HookHealth.Failed, _source.Health);
        Assert.Equal("The input hook stopped.", _source.Detail);

        _source.Maintain();

        Assert.Equal(HookHealth.Ok, _source.Health);
        Assert.True(old.Disposed);
        fresh.Raise(F8Down);
        Assert.Equal([F8Down], _heard);
    }

    [Fact]
    public void While_the_hook_is_the_fallback_raw_input_is_tried_again_and_wins_the_job_back()
    {
        RawThatStartsAs(HookHealth.Failed);
        var hook = HookThatStartsAs(HookHealth.Ok);
        var secondRaw = RawThatStartsAs(HookHealth.Ok);
        _source.Start();

        _source.Maintain();

        Assert.Equal(2, _raws.Count);
        Assert.False(_source.UsingFallback);
        Assert.True(hook.Disposed);
        secondRaw.Raise(F8Down);
        Assert.Equal([F8Down], _heard);
    }

    [Fact]
    public void Maintenance_before_start_and_after_stop_does_nothing()
    {
        _source.Maintain();
        Assert.Empty(_raws);

        RawThatStartsAs(HookHealth.Failed);
        _source.Start();
        _source.Stop();
        var made = _raws.Count + _hooks.Count;
        _source.Maintain();

        Assert.Equal(made, _raws.Count + _hooks.Count);
    }

    [Fact]
    public void Stopping_lets_go_of_everything_and_says_so()
    {
        var raw = RawThatStartsAs(HookHealth.Failed);
        var hook = HookThatStartsAs(HookHealth.Ok);
        _source.Start();

        _source.Stop();

        Assert.True(raw.Disposed);
        Assert.True(hook.Disposed);
        Assert.Equal(HookHealth.Stopped, _source.Health);
        Assert.False(_source.UsingFallback);
    }

    [Fact]
    public void A_start_after_a_stop_begins_again_with_raw_input()
    {
        RawThatStartsAs(HookHealth.Ok);
        _source.Start();
        _source.Stop();
        var again = RawThatStartsAs(HookHealth.Ok);

        _source.Start();
        again.Raise(F8Down);

        Assert.Equal([F8Down], _heard);
        Assert.Equal(HookHealth.Ok, _source.Health);
    }

    [Fact]
    public void Marking_failed_and_recovering_reach_the_source_that_is_delivering()
    {
        var raw = RawThatStartsAs(HookHealth.Ok);
        raw.Recovers = true;
        _source.Start();

        Assert.True(_source.TryRecover());
        _source.MarkFailed("It stopped.");

        Assert.Equal("It stopped.", raw.MarkedFailed);
    }
}
