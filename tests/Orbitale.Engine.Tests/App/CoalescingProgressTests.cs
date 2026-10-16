using Orbitale.App.Services;

namespace Orbitale.Engine.Tests.App;

public sealed class CoalescingProgressTests
{
    /// <summary>A UI dispatcher whose queue the test drains by hand (a slow or busy UI thread).</summary>
    sealed class ManualDispatcher : IUiDispatcher
    {
        readonly Queue<Action> _queue = new();
        public int Enqueued { get; private set; }
        public bool HasThreadAccess => true;

        public bool TryEnqueue(Action action)
        {
            lock (_queue) { _queue.Enqueue(action); Enqueued++; }
            return true;
        }

        public int Pending { get { lock (_queue) return _queue.Count; } }

        public void RunAll()
        {
            while (true)
            {
                Action next;
                lock (_queue) { if (_queue.Count == 0) return; next = _queue.Dequeue(); }
                next();
            }
        }
    }

    sealed record Report(int Index, string Payload);

    static (CoalescingProgress<Report, int> Progress, List<(int[] Steps, Report Latest)> Applied, ManualDispatcher Ui) Create()
    {
        var ui = new ManualDispatcher();
        var applied = new List<(int[] Steps, Report Latest)>();
        var progress = new CoalescingProgress<Report, int>(ui, r => r.Index, (steps, latest) => applied.Add((steps.ToArray(), latest)));
        return (progress, applied, ui);
    }

    [Fact]
    public void Reports_while_the_UI_is_busy_are_applied_once_with_the_latest_value_and_every_step_in_order()
    {
        var (progress, applied, ui) = Create();

        progress.Report(new Report(0, "a"));
        progress.Report(new Report(1, "b"));
        progress.Report(new Report(2, "c"));
        Assert.Equal(1, ui.Pending);   // one drain is scheduled, not three
        ui.RunAll();

        var only = Assert.Single(applied);
        Assert.Equal([0, 1, 2], only.Steps);
        Assert.Equal("c", only.Latest.Payload);
    }

    [Fact]
    public void A_report_after_a_drain_schedules_a_new_one()
    {
        var (progress, applied, ui) = Create();

        progress.Report(new Report(0, "a"));
        ui.RunAll();
        progress.Report(new Report(1, "b"));
        ui.RunAll();

        Assert.Equal(2, ui.Enqueued);
        Assert.Equal([[0], [1]], applied.Select(a => a.Steps));
        Assert.Equal(["a", "b"], applied.Select(a => a.Latest.Payload));
    }

    [Fact]
    public void Flush_applies_what_is_pending_now_and_the_scheduled_drain_then_does_nothing()
    {
        var (progress, applied, ui) = Create();

        progress.Report(new Report(4, "x"));
        progress.Flush();
        Assert.Single(applied);
        ui.RunAll();

        Assert.Single(applied);
        progress.Flush();   // nothing pending: no empty apply
        Assert.Single(applied);
    }

    [Fact]
    public void Close_drops_what_is_pending_and_ignores_later_reports()
    {
        var (progress, applied, ui) = Create();

        progress.Report(new Report(0, "a"));
        progress.Close();
        progress.Report(new Report(1, "b"));
        ui.RunAll();
        progress.Flush();

        Assert.Empty(applied);
        Assert.Equal(1, ui.Enqueued);
    }

    [Fact]
    public void A_refused_enqueue_does_not_wedge_the_pump()
    {
        var refusing = true;
        var queue = new List<Action>();
        var ui = new LambdaDispatcher(a => { if (refusing) return false; queue.Add(a); return true; });
        var applied = new List<int[]>();
        var progress = new CoalescingProgress<Report, int>(ui, r => r.Index, (steps, _) => applied.Add(steps.ToArray()));

        progress.Report(new Report(0, "a"));   // the dispatcher is shutting down
        refusing = false;
        progress.Report(new Report(1, "b"));
        foreach (var a in queue) a();

        Assert.Equal([[0, 1]], applied);
    }

    sealed class LambdaDispatcher(Func<Action, bool> enqueue) : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) => enqueue(action);
    }

    [Fact]
    public async Task Concurrent_reports_deliver_every_step_exactly_once()
    {
        var (progress, applied, ui) = Create();
        const int perThread = 500;

        var writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++) progress.Report(new Report(t * perThread + i, "p"));
        })).ToArray();
        while (!writers.All(w => w.IsCompleted))
        {
            ui.RunAll();
            await Task.Yield();
        }
        await Task.WhenAll(writers);
        ui.RunAll();

        var all = applied.SelectMany(a => a.Steps).ToList();
        Assert.Equal(4 * perThread, all.Count);
        Assert.Equal(Enumerable.Range(0, 4 * perThread), all.Order());
        Assert.True(applied.Count <= ui.Enqueued);
    }
}
