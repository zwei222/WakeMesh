using WakeMesh.Mobile.Services;

namespace WakeMesh.Mobile.Tests;

public class T136RingingInteractionTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(69, 0.69)] [InlineData(70, 0.70)] [InlineData(100, 1)]
    public void Progress_is_clamped(double delta, double expected) => Assert.Equal(expected, SlideMath.Progress(delta, 100), 2);
    [Fact] public void Commit_threshold_is_seventy_percent() { Assert.False(SlideMath.IsCommitted(.69)); Assert.True(SlideMath.IsCommitted(.70)); }
    [Fact] public void Alpha_remains_visible_at_threshold() { Assert.Equal(1, SlideMath.LabelAlpha(0)); Assert.Equal(1, SlideMath.LabelAlpha(.70)); Assert.Equal(1, SlideMath.LabelAlpha(1)); Assert.True(SlideMath.LabelAlpha(.69) >= .5); }
    [Fact] public void Vertical_motion_yields_to_scroll() => Assert.True(SlideMath.IsVerticalGesture(9, 18, 8));
    [Fact] public void Pending_blocks_duplicate_and_snooze_requires_outcome() { var s = new RingingInteractionState(); Assert.True(s.Begin("A", RingingAction.Snooze)); Assert.False(s.Begin("A", RingingAction.Stop)); Assert.False(s.OnSnoozeOutcome("A", true, true)); Assert.True(s.OnSnoozeOutcome("A", true, false)); }
    [Fact] public void Failed_snooze_can_retry_and_old_failure_does_not_clear_new_send() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Snooze); s.Begin("A", RingingAction.Snooze, true); Assert.False(s.OnSnoozeOutcome("A", false, true)); Assert.Equal(1, s.OutstandingSends("A")); }
    [Fact] public void Stop_succeeds_only_after_set_removal() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Stop); Assert.False(s.ObserveActive("A", true)); Assert.True(s.ObserveActive("A", false)); }
    [Fact] public void All_stop_requires_observed_empty_set() { var s = new RingingInteractionState(); s.BeginAllStop(); Assert.False(s.ObserveAllStop(1)); Assert.True(s.ObserveAllStop(0)); }
    [Fact] public void Stop_supersedes_timed_out_snooze_and_ignores_old_success() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Snooze); s.Timeout("A"); Assert.True(s.Begin("A", RingingAction.Stop)); Assert.False(s.OnSnoozeOutcome("A", true, false)); Assert.Equal(RingingAction.Stop, s.PendingAction("A")); Assert.True(s.ObserveActive("A", false)); }
    [Fact] public void Old_snooze_failure_does_not_clear_stop_or_duplicate_stop() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Snooze); s.Timeout("A"); s.Begin("A", RingingAction.Stop); Assert.False(s.OnSnoozeOutcome("A", false, true)); Assert.Equal(RingingAction.Stop, s.PendingAction("A")); Assert.False(s.Begin("A", RingingAction.Stop)); }
    [Fact] public void Forget_clears_operation_before_reusing_id() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Snooze); s.Forget("A"); Assert.Null(s.PendingAction("A")); Assert.Equal(0, s.OutstandingSends("A")); Assert.True(s.Begin("A", RingingAction.Stop)); }
    [Fact] public void All_stop_timeout_is_generation_scoped() { var s = new RingingInteractionState(); var g1 = s.BeginAllStop(); var g2 = s.BeginAllStop(); Assert.False(s.TimeoutAllStop(g1)); Assert.True(s.AllStopPending); Assert.True(s.TimeoutAllStop(g2)); Assert.False(s.ObserveAllStop(0)); }
    [Fact] public void Snooze_disappearance_alone_keeps_operation_pending() { var s = new RingingInteractionState(); s.Begin("A", RingingAction.Snooze); Assert.False(s.ObserveActive("A", false)); Assert.Equal(RingingAction.Snooze, s.PendingAction("A")); }
}
