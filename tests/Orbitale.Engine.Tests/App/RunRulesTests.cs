using Orbitale.App.ViewModels;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Stacking;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Tests.App;

public sealed class RunGateTests
{
    [Theory]
    [InlineData(SessionState.Idle, true)]
    [InlineData(SessionState.Finished, true)]
    [InlineData(SessionState.Stopped, true)]
    [InlineData(SessionState.Running, false)]
    [InlineData(SessionState.Stopping, false)]
    public void Start_follows_the_session_state(SessionState state, bool expected) =>
        Assert.Equal(expected, RunGate.CanStart(state, hasFrames: true, loadingFrames: false, resultBusy: false, frameDecoding: false));

    [Fact]
    public void Start_needs_frames_and_waits_for_loading_and_for_any_use_of_the_result()
    {
        Assert.False(RunGate.CanStart(SessionState.Idle, hasFrames: false, loadingFrames: false, resultBusy: false, frameDecoding: false));
        Assert.False(RunGate.CanStart(SessionState.Idle, hasFrames: true, loadingFrames: true, resultBusy: false, frameDecoding: false));
        // Start disposes the previous result: never while it is being saved or rendered.
        Assert.False(RunGate.CanStart(SessionState.Finished, hasFrames: true, loadingFrames: false, resultBusy: true, frameDecoding: false));
    }

    [Fact]
    public void Start_waits_for_an_original_frame_being_decoded()
    {
        // A 40 MP frame decodes to ~0.5 GB as float: starting a run meanwhile would push the peak past 8 GB.
        Assert.False(RunGate.CanStart(SessionState.Finished, hasFrames: true, loadingFrames: false, resultBusy: false, frameDecoding: true));
        Assert.False(RunGate.CanStart(SessionState.Idle, hasFrames: true, loadingFrames: false, resultBusy: false, frameDecoding: true));
    }

    [Theory]
    [InlineData(SessionState.Idle, false, false, CloseAction.Close)]
    [InlineData(SessionState.Finished, false, false, CloseAction.Close)]       // the result was saved
    [InlineData(SessionState.Stopped, false, false, CloseAction.Close)]
    [InlineData(SessionState.Finished, true, false, CloseAction.WaitForSave)]  // never lose the file being written
    [InlineData(SessionState.Finished, true, true, CloseAction.WaitForSave)]
    [InlineData(SessionState.Running, false, false, CloseAction.ConfirmStopRun)]
    [InlineData(SessionState.Stopping, false, false, CloseAction.ConfirmStopRun)]
    [InlineData(SessionState.Finished, false, true, CloseAction.ConfirmDiscardResult)]
    [InlineData(SessionState.Stopped, false, true, CloseAction.ConfirmDiscardResult)]
    public void Closing_the_window(SessionState state, bool saving, bool unsavedResult, CloseAction expected) =>
        Assert.Equal(expected, RunGate.OnClose(state, saving, unsavedResult));

    [Fact]
    public void Close_prompts_have_their_texts()
    {
        Assert.Equal("Stop the run and close?", RunText.ClosePrompt(CloseAction.ConfirmStopRun));
        Assert.Equal("Close without saving the result?", RunText.ClosePrompt(CloseAction.ConfirmDiscardResult));
        Assert.Null(RunText.ClosePrompt(CloseAction.Close));
        Assert.Null(RunText.ClosePrompt(CloseAction.WaitForSave));
        Assert.StartsWith("Finishing save", RunText.FinishingSave);
    }

    [Theory]
    [InlineData(SessionState.Finished, true, false, true)]
    [InlineData(SessionState.Stopped, true, false, true)]
    [InlineData(SessionState.Stopped, true, true, false)]
    [InlineData(SessionState.Finished, false, false, false)]
    [InlineData(SessionState.Idle, true, false, false)]
    [InlineData(SessionState.Running, true, false, false)]
    [InlineData(SessionState.Stopping, true, false, false)]
    public void Save_needs_a_kept_result_and_no_save_in_flight(SessionState state, bool hasResult, bool saving, bool expected) =>
        Assert.Equal(expected, RunGate.CanSave(state, hasResult, saving));

    [Theory]
    [InlineData(SessionState.Idle, false, false, true)]
    [InlineData(SessionState.Stopped, false, false, true)]
    [InlineData(SessionState.Running, false, false, false)]
    [InlineData(SessionState.Stopping, false, false, false)]
    [InlineData(SessionState.Finished, true, false, false)]
    [InlineData(SessionState.Finished, false, true, false)]
    public void Frames_can_be_edited_only_when_nothing_runs_loads_or_saves(SessionState state, bool loading, bool saving, bool expected) =>
        Assert.Equal(expected, RunGate.CanEditFrames(state, loading, saving));

    [Fact]
    public void Stop_only_while_running_and_frames_can_be_viewed_only_when_not_running()
    {
        Assert.True(RunGate.CanStop(SessionState.Running));
        Assert.False(RunGate.CanStop(SessionState.Stopping));
        Assert.False(RunGate.CanStop(SessionState.Finished));
        Assert.True(RunGate.IsRunning(SessionState.Stopping));
        Assert.False(RunGate.CanViewFrame(SessionState.Running));
        Assert.False(RunGate.CanViewFrame(SessionState.Stopping));
        Assert.True(RunGate.CanViewFrame(SessionState.Finished));
        Assert.True(RunGate.CanViewFrame(SessionState.Idle));
    }
}

public sealed class RunTextTests
{
    static StackProgress Progress(int processed, int total, int trailsTotal, TimeSpan? eta) =>
        new(processed - 1, processed, total, 0, trailsTotal, eta, new PreviewImage(1, 1, new byte[4]), Array.Empty<LineSegment>(), null);

    [Fact]
    public void Status_while_running_counts_frames_trails_and_time_left()
    {
        Assert.Equal("141 / 279 · 37 trails removed · ~2 min left", RunText.Status(Progress(141, 279, 37, TimeSpan.FromSeconds(100))));
        Assert.Equal("1 / 279 · 1 trail removed · ~45 s left", RunText.Status(Progress(1, 279, 1, TimeSpan.FromSeconds(44.2))));
        Assert.Equal("2 / 279 · 0 trails removed · ~1 h 5 min left", RunText.Status(Progress(2, 279, 0, TimeSpan.FromMinutes(65))));
        Assert.Equal("279 / 279 · 3 trails removed", RunText.Status(Progress(279, 279, 3, TimeSpan.Zero)));
        Assert.Equal("5 / 9 · 3 trails removed", RunText.Status(Progress(5, 9, 3, null)));
        Assert.Equal("5 / 9 · 3 trails removed · 2 skipped (no sky alignment)", RunText.Status(Progress(5, 9, 3, null) with { TrailRemovalSkipped = 2 }));
    }

    [Fact]
    public void End_of_run_texts()
    {
        Assert.Equal("Done · 279 frames · 412 trails removed", RunText.Finished(279, 412));
        Assert.Equal("Done · 1 frame · 1 trail removed", RunText.Finished(1, 1));
        Assert.Equal("Stopped at 141 / 279 · result kept", RunText.Stopped(141, 279));
        Assert.Equal("Done · 279 frames · 412 trails removed", RunText.Finished(279, 412, trailRemovalSkipped: 0));
        Assert.Equal("Done · 279 frames · 412 trails removed · trail removal skipped on 3 frames (sky alignment failed)",
            RunText.Finished(279, 412, trailRemovalSkipped: 3));
        Assert.Equal("Stopped at 141 / 279 · result kept · trail removal skipped on 1 frame (sky alignment failed)",
            RunText.Stopped(141, 279, trailRemovalSkipped: 1));
    }

    [Fact]
    public void Badges_and_percent()
    {
        Assert.Equal("Live · frame 141 / 279", RunText.LiveBadge(141, 279));
        Assert.Equal("Result · 279 frames", RunText.ResultBadge(true, 279, 279));
        Assert.Equal("Stopped result · 141 / 279 frames", RunText.ResultBadge(false, 141, 279));
        Assert.Equal(50, RunText.Percent(5, 10));
        Assert.Equal(0, RunText.Percent(0, 0));
    }

    [Fact]
    public void Default_save_name_is_the_first_frame_name_with_the_suffix()
    {
        Assert.Equal("DSCF0001_orbitale.tif", RunText.DefaultSaveName(@"D:\x\DSCF0001.JPG", "tif"));
        Assert.Equal("DSCF0001_orbitale.jpg", RunText.DefaultSaveName(@"D:\x\DSCF0001.JPG", "jpg"));
        Assert.Equal("orbitale.tif", RunText.DefaultSaveName(null, "tif"));
    }
}

public sealed class NoticeLogTests
{
    [Fact]
    public void Shows_the_latest_notice_and_counts_them()
    {
        var log = new NoticeLog();
        Assert.False(log.HasNotice);

        log.Add("first");
        Assert.Equal("first", log.Latest);
        Assert.Equal("Notice", log.Title);

        log.Add("second");
        log.Add(null);
        log.Add("  ");
        Assert.Equal("second", log.Latest);
        Assert.Equal(2, log.Count);
        Assert.Equal("2 notices · showing the latest", log.Title);

        log.Clear();
        Assert.False(log.HasNotice);
        Assert.Equal(0, log.Count);
    }
}

public sealed class StackSettingsMapperTests
{
    [Theory]
    [InlineData(BlendMode.Darken)]
    [InlineData(BlendMode.Average)]
    public void Comet_and_gap_filling_are_turned_off_outside_lighten(BlendMode mode)
    {
        var s = StackSettingsMapper.ForRun(new StackSettings(mode, Comet: true, GapFill: true, CometStrength: 0.3, HeadAtStart: true));

        Assert.False(s.Comet);
        Assert.False(s.GapFill);
        Assert.Equal(mode, s.Mode);
        Assert.Equal(0.3, s.CometStrength);   // strengths are remembered
        Assert.False(s.HeadAtStart);          // no comet, so no head to place
    }

    [Theory]
    [InlineData(BlendMode.Lighten)]
    [InlineData(BlendMode.Darken)]
    public void Head_at_start_is_off_when_the_comet_is_off(BlendMode mode)
    {
        // HeadAtStart only places the comet head: without a comet the run keeps file order.
        var s = StackSettingsMapper.ForRun(new StackSettings(mode, Comet: false, HeadAtStart: true));
        Assert.False(s.HeadAtStart);
        Assert.True(StackSettingsMapper.ForRun(new StackSettings(BlendMode.Lighten, Comet: true, HeadAtStart: true)).HeadAtStart);
    }

    [Fact]
    public void Lighten_keeps_comet_and_gap_filling()
    {
        var s = StackSettingsMapper.ForRun(new StackSettings(BlendMode.Lighten, Comet: true, GapFill: true));
        Assert.True(s.Comet);
        Assert.True(s.GapFill);
    }

    [Fact]
    public void Percent_sliders_map_to_fractions()
    {
        var s = StackSettingsMapper.FromPercent(new StackSettings(NeighbourK: 3), BlendMode.Lighten,
            comet: true, cometStrength: 50, headAtStart: false, gapFill: true, gapFillStrength: 25, removeTrails: true, trailSensitivity: 60);

        Assert.Equal(new StackSettings(BlendMode.Lighten, true, 0.5, false, true, 0.25, true, 0.6, NeighbourK: 3), s);
    }
}

public sealed class ResultReadersTests
{
    [Fact]
    public void Start_stays_blocked_until_every_overlapping_render_has_finished()
    {
        var readers = new ResultReaders();
        bool CanStart() => RunGate.CanStart(SessionState.Finished, hasFrames: true, loadingFrames: false, resultBusy: readers.InUse, frameDecoding: false);

        readers.Begin();   // render #1, at the end of the run
        readers.Begin();   // render #2, "Back to result" after viewing a frame
        readers.End();     // render #1 finishes (superseded) while #2 still reads the result
        Assert.True(readers.InUse);
        Assert.False(CanStart());

        readers.End();     // render #2 finishes
        Assert.False(readers.InUse);
        Assert.True(CanStart());
    }

    [Fact]
    public void An_unbalanced_end_never_goes_below_zero()
    {
        var readers = new ResultReaders();
        readers.End();
        readers.Begin();
        Assert.True(readers.InUse);
    }
}
