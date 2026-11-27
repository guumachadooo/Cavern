using System.Numerics;

using Cavern;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 5 adapter wrapping Cavern's active <see cref="Listener"/> and <see cref="VirtualizerFilter"/>,
/// driving Slot 5A physical externalization (Allen &amp; Berkley ISM early reflections, Duda &amp; Martens near-field DVF,
/// ISO 9613-1 air absorption) with an automatic BRIR double-room bypass guard.
/// </summary>
public sealed class CavernVirtualizerRendererAdapter : IBinauralRenderer {
    Listener boundListener;
    bool binauralActive = true;
    int sampleRate = VirtualizerFilter.FilterSampleRate;
    string profileName = "Cavern Virtualizer";

    PhysicalExternalizationConfig externalizationConfig = PhysicalExternalizationConfig.Default;
    bool externalBrirFlag;
    Vector3 lastEnvironmentSize = new(4f, 2.8f, 4f);
    float lastLyricRoomScale = 1f;
    PhysicalExternalizationDiagnostics diagnostics = PhysicalExternalizationDiagnostics.Default;

    // Zero-GC scratch buffers for projecting close (< 1.0 m) physical sources onto active virtual HRIR speakers
    readonly float[] channelMinDistances = new float[64];
    readonly Vector3[] channelUnitVectors = new Vector3[64];
    int cachedChannelCount;

    /// <inheritdoc/>
    public string RendererId => "cavern-virtualizer";

    /// <inheritdoc/>
    public string ProfileName => profileName;

    /// <inheritdoc/>
    public int SampleRate => boundListener != null ? boundListener.SampleRate : sampleRate;

    /// <inheritdoc/>
    public int ProfileVersion => VirtualizerFilter.ProfileVersion;

    /// <inheritdoc/>
    public bool IsBinauralActive => binauralActive;

    /// <summary>
    /// Latest Slot 5A physical externalization diagnostics snapshot.
    /// </summary>
    public PhysicalExternalizationDiagnostics Diagnostics => diagnostics;

    /// <summary>
    /// Updates the bound <see cref="Listener"/>, binaural state, and active profile display name.
    /// </summary>
    public void Bind(Listener listener, bool isBinauralActive, string activeProfileName = null) {
        boundListener = listener;
        binauralActive = isBinauralActive;
        if (listener != null) {
            sampleRate = listener.SampleRate;
        }
        if (!string.IsNullOrWhiteSpace(activeProfileName)) {
            profileName = activeProfileName;
        }
    }

    /// <summary>
    /// Applies Slot 5A physical externalization parameters to <see cref="VirtualizerFilter"/> and refreshes diagnostics,
    /// enforcing the automatic BRIR double-room bypass guard whenever a measured BRIR is active.
    /// </summary>
    public PhysicalExternalizationDiagnostics ApplyExternalization(
        PhysicalExternalizationConfig config,
        bool isMeasuredBrirProfile,
        Vector3 environmentSize) {
        externalizationConfig = config;
        externalBrirFlag = isMeasuredBrirProfile;
        if (environmentSize.X > 0.1f && environmentSize.Y > 0.1f && environmentSize.Z > 0.1f) {
            lastEnvironmentSize = environmentSize;
        }

        VirtualizerFilter.ConfigureExternalization(
            ismEnabled: config.IsmRoomEnabled && binauralActive,
            preset: config.RoomPreset,
            wallMaterial: config.WallMaterial,
            reflectionOrder: config.ReflectionOrder,
            reflectionLevel: Math.Clamp(config.ReflectionLevel * lastLyricRoomScale, 0f, 1.5f),
            nearFieldEnabled: config.NearFieldEnabled && binauralActive,
            airAbsorptionEnabled: config.AirAbsorptionEnabled,
            airTempCelsius: config.AirTemperatureCelsius,
            airRelativeHumidityPct: config.AirRelativeHumidityPct,
            customEnvironmentHalfSize: environmentSize,
            externalBrirGuard: isMeasuredBrirProfile);

        diagnostics = BuildDiagnosticsSnapshot();
        return diagnostics;
    }

    /// <summary>
    /// Modulates live ISM reflection ambience smoothly from distilled <see cref="LyricSpatialResponseFx"/>
    /// with zero allocations, respecting BRIR double-room bypass guards.
    /// </summary>
    public void ApplyLiveLyricRoomModulation(float roomReflectionScale) {
        float clampedScale = Math.Clamp(roomReflectionScale, 0.45f, 1.45f);
        if (MathF.Abs(clampedScale - lastLyricRoomScale) < 0.015f) {
            return;
        }
        lastLyricRoomScale = clampedScale;
        if (!binauralActive || !externalizationConfig.IsmRoomEnabled || externalBrirFlag || VirtualizerFilter.IsMeasuredBrirActive) {
            return;
        }
        VirtualizerFilter.ConfigureExternalization(
            ismEnabled: true,
            preset: externalizationConfig.RoomPreset,
            wallMaterial: externalizationConfig.WallMaterial,
            reflectionOrder: externalizationConfig.ReflectionOrder,
            reflectionLevel: Math.Clamp(externalizationConfig.ReflectionLevel * clampedScale, 0f, 1.5f),
            nearFieldEnabled: externalizationConfig.NearFieldEnabled,
            airAbsorptionEnabled: AirAbsorptionFilter.GlobalEnabled,
            airTempCelsius: externalizationConfig.AirTemperatureCelsius,
            airRelativeHumidityPct: externalizationConfig.AirRelativeHumidityPct,
            customEnvironmentHalfSize: lastEnvironmentSize,
            externalBrirGuard: false);
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, Vector3 environmentSize, Environments environmentType) {
        this.sampleRate = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
        Listener.EnvironmentType = environmentType;
        if (environmentSize.X > 0.1f && environmentSize.Y > 0.1f && environmentSize.Z > 0.1f) {
            Listener.EnvironmentSize = environmentSize;
        }
    }

    /// <inheritdoc/>
    public int RenderFrame(SpatialSceneFrame frame, float[] interleavedStereoOut, int outputOffset, int maxFrames) {
        if (boundListener == null || interleavedStereoOut == null || maxFrames <= 0) {
            return 0;
        }

        int updateRate = Math.Max(16, boundListener.UpdateRate);
        int chunks = maxFrames / updateRate;
        if (chunks <= 0) {
            return 0;
        }

        if (binauralActive && frame != null) {
            SyncLiveAcousticFrame(frame);
        }

        float[] rendered = boundListener.Render(chunks);
        if (rendered == null) {
            return 0;
        }

        int stride = Listener.Channels.Length;
        int blockFrames = chunks * updateRate;
        for (int s = 0; s < blockFrames; s++) {
            float left = rendered[s * stride];
            float right = stride > 1 ? rendered[s * stride + 1] : left;
            interleavedStereoOut[outputOffset + s * 2] = float.IsFinite(left) ? left : 0f;
            interleavedStereoOut[outputOffset + s * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        if (frame != null) {
            frame.OutputFrameCount = blockFrames;
        }
        diagnostics = BuildDiagnosticsSnapshot();
        return blockFrames;
    }

    /// <inheritdoc/>
    public void Reset() {
        for (int i = 0; i < channelMinDistances.Length; i++) {
            channelMinDistances[i] = float.PositiveInfinity;
        }
        VirtualizerFilter.SetLiveChannelMinDistances(channelMinDistances, cachedChannelCount);
    }

    /// <inheritdoc/>
    public void Dispose() {
        boundListener = null;
    }

    void SyncLiveAcousticFrame(SpatialSceneFrame frame) {
        Vector3 envSize = frame.EnvironmentSize;
        Vector3 invEnv = new(
            envSize.X > 0.01f ? 1f / envSize.X : 0.1f,
            envSize.Y > 0.01f ? 1f / envSize.Y : 0.142857f,
            envSize.Z > 0.01f ? 1f / envSize.Z : 0.1f);

        Vector3 listenerNormPos = frame.ListenerPose.Position * invEnv;
        Vector3 listenerRotDeg = frame.ListenerPose.EulerDegrees;
        VirtualizerFilter.LiveListenerNormalizedPosition = listenerNormPos;
        VirtualizerFilter.LiveListenerRotationDegrees = listenerRotDeg;

        Channel[] channels = Listener.Channels;
        int chCount = Math.Min(channels.Length, channelMinDistances.Length);
        cachedChannelCount = chCount;
        for (int i = 0; i < chCount; i++) {
            channelMinDistances[i] = float.PositiveInfinity;
            channelUnitVectors[i] = channels[i].SphericalPos;
        }

        if (!externalizationConfig.NearFieldEnabled || frame.Sources == null) {
            VirtualizerFilter.SetLiveChannelMinDistances(channelMinDistances, chCount);
            return;
        }

        Vector3 physHalf = VirtualizerFilter.ActiveRoomGeometry.HalfDimensionsMeters;
        IReadOnlyList<SpatialSourceDescriptor> sources = frame.Sources;
        for (int s = 0; s < sources.Count; s++) {
            SpatialSourceDescriptor src = sources[s];
            if (src == null || src.IsLfe || src.IsMuted || src.IsDynamicallyFilteredOut) {
                continue;
            }
            Vector3 normDelta = src.NormalizedPosition - listenerNormPos;
            Vector3 physDeltaWorld = normDelta * physHalf;
            float distMeters = physDeltaWorld.Length();
            if (distMeters >= NearFieldHeadFilter.NearFieldThresholdMeters) {
                continue;
            }

            Vector3 dirHead = distMeters > 1e-4f
                ? (physDeltaWorld / distMeters).RotateInverse(listenerRotDeg)
                : Vector3.UnitZ;

            int bestCh = -1;
            float bestDot = -2f;
            for (int c = 0; c < chCount; c++) {
                if (channels[c].LFE) {
                    continue;
                }
                float d = Vector3.Dot(dirHead, channelUnitVectors[c]);
                if (d > bestDot) {
                    bestDot = d;
                    bestCh = c;
                }
            }
            if (bestCh >= 0 && distMeters < channelMinDistances[bestCh]) {
                channelMinDistances[bestCh] = distMeters;
            }
        }

        VirtualizerFilter.SetLiveChannelMinDistances(channelMinDistances, chCount);
    }

    PhysicalExternalizationDiagnostics BuildDiagnosticsSnapshot() {
        bool brirActive = externalBrirFlag || VirtualizerFilter.IsMeasuredBrirActive;
        bool ismRequested = externalizationConfig.IsmRoomEnabled;
        bool directBypassed = !binauralActive;
        bool brirBypassed = ismRequested && binauralActive && brirActive;
        bool ismActive = ismRequested && binauralActive && !brirActive;
        IsmRoomGeometry geom = VirtualizerFilter.ActiveRoomGeometry;

        return new PhysicalExternalizationDiagnostics {
            IsIsmRequested = ismRequested,
            IsIsmActive = ismActive,
            IsBrirGuardBypassed = brirBypassed,
            IsDirectStereoBypassed = directBypassed,
            ActiveReflectionCount = ismActive
                ? (externalizationConfig.ReflectionOrder >= 2 ? IsmRoomEngine.Order2ReflectionCount : IsmRoomEngine.Order1ReflectionCount)
                : 0,
            SabineRt60Seconds = geom.SabineRt60Seconds,
            EyringRt60Seconds = geom.EyringRt60Seconds,
            RoomDimensionsMeters = geom.DimensionsMeters,
            NearFieldEnabled = externalizationConfig.NearFieldEnabled && binauralActive,
            NearFieldEngaged = binauralActive && externalizationConfig.NearFieldEnabled && VirtualizerFilter.LastBlockNearFieldActive,
            AirAbsorptionEnabled = externalizationConfig.AirAbsorptionEnabled,
            AirTemperatureCelsius = externalizationConfig.AirTemperatureCelsius,
            AirRelativeHumidityPct = externalizationConfig.AirRelativeHumidityPct
        };
    }
}

