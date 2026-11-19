namespace WakeMesh.Mobile.Services;

public enum RingingAction { Stop, Snooze }

public static class SlideMath
{
    public static double Progress(double deltaX, double travel) => travel <= 0 ? 0 : Math.Clamp(deltaX / travel, 0, 1);
    public static bool IsCommitted(double progress) => progress >= .70;
    public static double LabelAlpha(double progress) => progress >= .70 ? 1 : 1 - (Math.Clamp(progress, 0, 1) / .70 * .5);
    public static bool IsVerticalGesture(double dx, double dy, double touchSlop) => Math.Abs(dy) > Math.Abs(dx) && Math.Abs(dy) > touchSlop;
}

public sealed class RingingInteractionState
{
    private sealed class Operation { public RingingAction Action; public bool Pending; public int Sends; }
    private readonly Dictionary<string, Operation> _operations = new();
    private bool _allStopPending;
    private int _allStopGen;
    public bool Begin(string id, RingingAction action, bool retry = false)
    {
        if (!_operations.TryGetValue(id, out var op)) _operations[id] = op = new Operation { Action = action };
        if (op.Pending && !retry) return false;
        if (action == RingingAction.Snooze) op.Sends++;
        op.Action = action; op.Pending = true; return true;
    }
    public int OutstandingSends(string id) => _operations.TryGetValue(id, out var op) ? op.Sends : 0;
    public RingingAction? PendingAction(string id) => _operations.TryGetValue(id, out var op) && op.Pending ? op.Action : null;
    public void Forget(string id) => _operations.Remove(id);
    public bool OnSnoozeOutcome(string id, bool success, bool isActive)
    {
        if (!_operations.TryGetValue(id, out var op)) return false;
        if (op.Sends > 0) op.Sends--;
        if (success && !isActive && op.Action == RingingAction.Snooze) { _operations.Remove(id); return true; }
        if (!success && op.Sends == 0 && op.Action == RingingAction.Snooze) { op.Pending = false; return false; }
        return false;
    }
    public bool ObserveActive(string id, bool isActive)
    {
        if (!_operations.TryGetValue(id, out var op) || op.Action != RingingAction.Stop || isActive) return false;
        _operations.Remove(id); return true;
    }
    public int BeginAllStop() { _allStopPending = true; return ++_allStopGen; }
    public bool AllStopPending => _allStopPending;
    public bool TimeoutAllStop(int gen) { if (!_allStopPending || gen != _allStopGen) return false; _allStopPending = false; return true; }
    public bool ObserveAllStop(int activeCount) { if (!_allStopPending || activeCount != 0) return false; _allStopPending = false; return true; }
    public void Timeout(string id) { if (_operations.TryGetValue(id, out var op)) op.Pending = false; }
}
