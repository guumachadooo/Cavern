using System;
using System.Numerics;
using System.Threading;

using Cavern;

using Cavernize.Logic.Models;

namespace Cavernize.Logic.Rendering;

public static partial class StudioBatchExporter {
    [ThreadStatic]
    static ListenerContext pendingExportContext;

    /// <summary>
    /// Creates an isolated <see cref="ListenerContext"/> for an offline Studio batch export job, ensuring valid
    /// room dimensions without mutating <see cref="ListenerContext.Default"/> or concurrent playback engines.
    /// </summary>
    internal static ListenerContext CreateExportContext(
        Vector3? environmentSize = null,
        Environments environmentType = Environments.Home,
        bool headphoneVirtualizer = false) {
        ListenerContext context = new(ListenerContext.Current) {
            EnvironmentType = environmentType,
            HeadphoneVirtualizer = headphoneVirtualizer
        };
        Vector3 size = environmentSize ?? context.EnvironmentSize;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) {
            size = new Vector3(10f, 7f, 10f);
        }
        context.EnvironmentSize = size;
        return context;
    }

    /// <summary>
    /// Creates a <see cref="Listener"/> explicitly bound to an isolated <paramref name="context"/> for offline export.
    /// </summary>
    internal static Listener CreateExportListener(
        ListenerContext context,
        int sampleRate,
        int updateRate = 64,
        QualityModes audioQuality = QualityModes.Perfect,
        int maximumSources = 256,
        bool lfeSeparation = true) => new(context ?? CreateExportContext(), loadGlobals: false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = audioQuality,
            MaximumSources = maximumSources,
            LFESeparation = lfeSeparation,
            Normalizer = 0f
        };

    /// <summary>
    /// Executes <see cref="ExportSingle(CavernizeTrack, StudioBatchExportJob, Action{double}, CancellationToken)"/>
    /// bound to an explicit caller-supplied <see cref="ListenerContext"/> for deterministic multi-job isolation.
    /// </summary>
    public static StudioBatchExportResult ExportSingle(
        CavernizeTrack track,
        StudioBatchExportJob job,
        ListenerContext exportContext,
        Action<double> onProgress = null,
        CancellationToken token = default) {
        ListenerContext previousPending = pendingExportContext;
        pendingExportContext = exportContext ?? CreateExportContext();
        try {
            return ExportSingle(track, job, onProgress, token);
        } finally {
            pendingExportContext = previousPending;
        }
    }

    /// <summary>
    /// Activates an isolated <see cref="ListenerContext"/> scope on the export thread so static <see cref="Listener"/>
    /// operations inside offline export pipelines never collide with concurrent playback or other exports.
    /// </summary>
    internal static ListenerContext.Scope BeginIsolatedExportScope(ListenerContext context = null) {
        ListenerContext resolved = context;
        if (resolved == null) {
            resolved = pendingExportContext;
            pendingExportContext = null;
        }
        resolved ??= CreateExportContext();
        if (resolved.EnvironmentSize.X <= 0f || resolved.EnvironmentSize.Y <= 0f || resolved.EnvironmentSize.Z <= 0f) {
            resolved.EnvironmentSize = new Vector3(10f, 7f, 10f);
        }
        return resolved.EnterScope();
    }

    /// <summary>
    /// Ensures the active <see cref="ListenerContext"/> has non-zero room dimensions.
    /// </summary>
    static void EnsureEnvironmentSize() {
        ListenerContext ctx = ListenerContext.Current;
        if (ctx.EnvironmentSize.X == 0 || ctx.EnvironmentSize.Y == 0 || ctx.EnvironmentSize.Z == 0) {
            ctx.EnvironmentSize = new Vector3(10, 7, 10);
        }
    }
}
