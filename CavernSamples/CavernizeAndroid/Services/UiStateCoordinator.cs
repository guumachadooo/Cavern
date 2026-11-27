namespace CavernizeAndroid.Services;

/// <summary>
/// Reactive state domains across Play, 3D Room, Studio/Export, and Settings.
/// </summary>
[Flags]
public enum UiStateDomain {
    None = 0,
    Playback = 1 << 0,
    Volume = 1 << 1,
    SpatialOutput = 1 << 2,
    Upmix = 1 << 3,
    Neural = 1 << 4,
    Room3D = 1 << 5,
    StudioExport = 1 << 6,
    Settings = 1 << 7,
    All = Playback | Volume | SpatialOutput | Upmix | Neural | Room3D | StudioExport | Settings
}

/// <summary>
/// Central reactive UI coordinator replacing scattered ad-hoc multi-refresh chains.
/// Coalesces domain invalidations into single-pass UI updates with a strict reentrancy loop guard.
/// </summary>
public sealed class UiStateCoordinator {
    const int MaxCascadePasses = 2;

    readonly List<(UiStateDomain Domain, int Order, Action Handler)> subscribers = [];
    Action<Action> postToUi;
    UiStateDomain pendingDomains;
    bool batchScheduled;
    bool isUpdatingFromCoordinator;
    bool disposed;

    /// <summary>
    /// True while the coordinator is synchronizing view widgets, preventing UI event handlers
    /// from re-emitting state changes back into the store/engine.
    /// </summary>
    public bool IsUpdatingFromCoordinator => isUpdatingFromCoordinator;

    /// <summary>Configures the UI-thread dispatcher used to coalesce batched refreshes.</summary>
    public void SetDispatcher(Action<Action> dispatcher) {
        postToUi = dispatcher;
    }

    /// <summary>Clears all registered domain subscribers (e.g., before rebuilding root UI on locale change).</summary>
    public void ClearSubscriptions() {
        subscribers.Clear();
        pendingDomains = UiStateDomain.None;
        batchScheduled = false;
    }

    /// <summary>Subscribes a deterministic handler to one or more <see cref="UiStateDomain"/> flags.</summary>
    public void Subscribe(UiStateDomain domain, Action handler, int order = 0) {
        if (handler == null || domain == UiStateDomain.None) {
            return;
        }
        subscribers.Add((domain, order, handler));
        subscribers.Sort(static (a, b) => a.Order.CompareTo(b.Order));
    }

    /// <summary>
    /// Executes programmatic UI synchronization under <see cref="IsUpdatingFromCoordinator"/>
    /// so widget change listeners cannot trigger feedback loops.
    /// </summary>
    public void RunGuarded(Action action) {
        if (action == null) {
            return;
        }
        bool prev = isUpdatingFromCoordinator;
        isUpdatingFromCoordinator = true;
        try {
            action();
        } finally {
            isUpdatingFromCoordinator = prev;
        }
    }

    /// <summary>
    /// Marks <paramref name="domains"/> dirty and dispatches a single-pass batched refresh
    /// (coalesced via UI dispatcher unless <paramref name="immediate"/> is true).
    /// </summary>
    public void NotifyChanged(UiStateDomain domains, bool immediate = false) {
        if (disposed || domains == UiStateDomain.None) {
            return;
        }
        pendingDomains |= domains;
        if (isUpdatingFromCoordinator) {
            return;
        }
        if (immediate || postToUi == null) {
            FlushPending();
            return;
        }
        if (!batchScheduled) {
            batchScheduled = true;
            postToUi(FlushPending);
        }
    }

    /// <summary>Flushes all accumulated dirty domains in deterministic subscriber order.</summary>
    public void FlushPending() {
        if (disposed || isUpdatingFromCoordinator || pendingDomains == UiStateDomain.None) {
            batchScheduled = false;
            return;
        }
        for (int pass = 0; pass < MaxCascadePasses && pendingDomains != UiStateDomain.None; pass++) {
            UiStateDomain batch = pendingDomains;
            pendingDomains = UiStateDomain.None;
            batchScheduled = false;

            isUpdatingFromCoordinator = true;
            try {
                int count = subscribers.Count;
                for (int i = 0; i < count; i++) {
                    var sub = subscribers[i];
                    if ((sub.Domain & batch) != 0) {
                        sub.Handler();
                    }
                }
            } finally {
                isUpdatingFromCoordinator = false;
            }
        }
        pendingDomains = UiStateDomain.None;
        batchScheduled = false;
    }

    /// <summary>Marks the coordinator disposed so late callbacks become no-ops.</summary>
    public void Dispose() {
        disposed = true;
        ClearSubscriptions();
        postToUi = null;
    }
}
