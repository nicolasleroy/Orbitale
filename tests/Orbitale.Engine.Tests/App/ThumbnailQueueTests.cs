using Orbitale.App.Services;

namespace Orbitale.Engine.Tests.App;

public sealed class ThumbnailQueueTests
{
    sealed class Slot(string path) : IThumbnailSlot<string>
    {
        public string Path { get; } = path;
        public string? Thumbnail { get; set; }
    }

    /// <summary>A fake loader: each call waits for the test to complete or fail it, and honours cancellation.</summary>
    sealed class FakeLoader
    {
        readonly Dictionary<string, TaskCompletionSource<string?>> _calls = [];
        public List<string> Started { get; } = [];
        public List<(string Path, Exception Error)> Errors { get; } = [];

        public Task<string?> Load(string path, CancellationToken token)
        {
            lock (_calls)
            {
                Started.Add(path);
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => tcs.TrySetCanceled(token));
                _calls[path] = tcs;
                return tcs.Task;
            }
        }

        public void Complete(string path) { lock (_calls) _calls[path].SetResult("img:" + path); }
        public void Fail(string path, Exception e) { lock (_calls) _calls[path].SetException(e); }

        public ThumbnailQueue<string> Queue(int maxConcurrent) =>
            new(Load, (p, e) => { lock (Errors) Errors.Add((p, e)); }, maxConcurrent);
    }

    [Fact]
    public async Task Loads_once_and_skips_slots_that_have_a_thumbnail_or_are_pending()
    {
        var fake = new FakeLoader();
        var queue = fake.Queue(4);
        var a = new Slot("a");

        var first = queue.RequestAsync(a);
        var duplicate = queue.RequestAsync(a);
        await duplicate;
        Assert.True(queue.IsPending(a));
        fake.Complete("a");
        await first;
        await queue.RequestAsync(a);

        Assert.Equal("img:a", a.Thumbnail);
        Assert.Equal(["a"], fake.Started);
        Assert.False(queue.IsPending(a));
    }

    [Fact]
    public async Task A_cancelled_waiting_load_never_runs_and_does_not_hold_up_the_queue()
    {
        var fake = new FakeLoader();
        var queue = fake.Queue(1);
        var a = new Slot("a");
        var scrolledAway = new Slot("b");
        var visible = new Slot("c");

        var ta = queue.RequestAsync(a);
        var tb = queue.RequestAsync(scrolledAway);
        var tc = queue.RequestAsync(visible);
        queue.Cancel(scrolledAway);
        await tb;
        fake.Complete("a");
        await ta;
        await WaitUntil(() => fake.Started.Contains("c"));
        fake.Complete("c");
        await tc;

        Assert.Equal(["a", "c"], fake.Started);
        Assert.Null(scrolledAway.Thumbnail);
        Assert.False(queue.IsPending(scrolledAway));
        Assert.Equal("img:c", visible.Thumbnail);
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task A_load_cancelled_while_running_is_silent_and_retried_on_the_next_request()
    {
        var fake = new FakeLoader();
        var queue = fake.Queue(4);
        var a = new Slot("a");

        var t = queue.RequestAsync(a);
        queue.Cancel(a);
        await t;
        Assert.Null(a.Thumbnail);
        Assert.False(queue.IsPending(a));
        Assert.Empty(fake.Errors);

        var retry = queue.RequestAsync(a);
        fake.Complete("a");
        await retry;
        Assert.Equal("img:a", a.Thumbnail);
        Assert.Equal(["a", "a"], fake.Started);
    }

    [Fact]
    public async Task A_failed_load_is_reported_never_thrown_and_retried_on_the_next_request()
    {
        var fake = new FakeLoader();
        var queue = fake.Queue(4);
        var a = new Slot("a");

        var t = queue.RequestAsync(a);
        fake.Fail("a", new InvalidOperationException("boom"));
        await t;
        Assert.Null(a.Thumbnail);
        Assert.False(queue.IsPending(a));
        var error = Assert.Single(fake.Errors);
        Assert.Equal("a", error.Path);
        Assert.IsType<InvalidOperationException>(error.Error);

        var retry = queue.RequestAsync(a);
        fake.Complete("a");
        await retry;
        Assert.Equal("img:a", a.Thumbnail);
    }

    [Fact]
    public async Task A_result_arriving_after_cancel_is_discarded()
    {
        // The loader ignores the token and returns anyway: the slot must stay empty.
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ThumbnailQueue<string>((_, _) => gate.Task, (_, e) => throw new Xunit.Sdk.XunitException(e.ToString()), 4);
        var a = new Slot("a");

        var t = queue.RequestAsync(a);
        queue.Cancel(a);
        gate.SetResult("late");
        await t;

        Assert.Null(a.Thumbnail);
        Assert.False(queue.IsPending(a));
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
