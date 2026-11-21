using System.Text;

using Android.Content;
using Android.Media;
using Android.OS;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

namespace CavernizeAndroid.Services;

/// <summary>
/// Snapshot of the currently active physical Android audio output route detected via <see cref="AudioManager"/>
/// and <see cref="AudioDeviceCallback"/>.
/// </summary>
public sealed class AudioDeviceRouteInfo {
    /// <summary>Stable normalized route key persisted in <see cref="AppSettingsStore"/> (e.g. "bt:galaxy_buds2_pro").</summary>
    public string DeviceKey { get; init; } = "speaker:builtin";

    /// <summary>Human-readable device label reported by Android.</summary>
    public string DisplayName { get; init; } = "Built-in Speaker";

    /// <summary>Detected hardware transport category.</summary>
    public AudioDeviceTransport Transport { get; init; } = AudioDeviceTransport.BuiltinSpeaker;

    /// <summary>True when the output is a personal headphone/IEM/DAC route suitable for binaural HRTF &amp; AutoEq.</summary>
    public bool IsHeadphoneRoute =>
        Transport == AudioDeviceTransport.BluetoothA2dp ||
        Transport == AudioDeviceTransport.BleAudio ||
        Transport == AudioDeviceTransport.UsbDac ||
        Transport == AudioDeviceTransport.WiredJack;

    /// <summary>Underlying Android <see cref="AudioDeviceInfo"/> handle for preferred USB DAC output routing.</summary>
    public AudioDeviceInfo NativeDevice { get; init; }

    /// <summary>Underlying Android <see cref="AudioDeviceInfo"/> handle for preferred pro-audio/USB microphone input routing.</summary>
    public AudioDeviceInfo NativeInputDevice { get; init; }

    /// <summary>Android <see cref="AudioDeviceInfo.Id"/> for output device (0 = system default).</summary>
    public int OutputDeviceId { get; init; }

    /// <summary>Android <see cref="AudioDeviceInfo.Id"/> for matched microphone input device (0 = system default).</summary>
    public int InputDeviceId { get; init; }

    /// <summary>Hardware sample rates advertised by this output route (empty = arbitrary rates supported).</summary>
    public int[] SupportedSampleRates { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Monitors Android audio output devices (Bluetooth A2DP, Bluetooth LE Audio, USB-C DAC/Headset, 3.5mm Wired,
/// Built-in Speaker) via native <see cref="AudioDeviceCallback"/> (no dangerous permissions required) and
/// automatically switches AutoEq + Binaural HRIR + Head-Tracking profiles per connected headphone.
/// </summary>
public sealed class AudioDeviceProfileManager : IDisposable {
    readonly Context context;
    readonly AppSettingsStore store;
    readonly RealtimePlayerEngine engine;
    readonly AudioManager audioManager;
    readonly Handler mainHandler;
    readonly RouteCallback deviceCallback;

    AudioDeviceRouteInfo currentDevice = new();
    bool registered;
    bool disposed;

    /// <summary>
    /// Raised on the main thread whenever the active audio output route changes or its profile is auto-applied.
    /// Parameters: (newDevice, appliedBindingOrNull, wasAutoMatchedNewHeadphone).
    /// </summary>
    public event Action<AudioDeviceRouteInfo, AudioDeviceProfileBinding, bool> ActiveDeviceChanged;

    /// <summary>Currently active physical audio output route.</summary>
    public AudioDeviceRouteInfo CurrentDevice => currentDevice;

    public AudioDeviceProfileManager(Context context, AppSettingsStore store, RealtimePlayerEngine engine) {
        this.context = context;
        this.store = store;
        this.engine = engine;
        audioManager = context.GetSystemService(Context.AudioService) as AudioManager;
        mainHandler = new Handler(Looper.MainLooper);
        deviceCallback = new RouteCallback(OnHardwareDevicesChanged);
    }

    /// <summary>
    /// Starts listening for real-time Android audio output connect/disconnect events and detects the active route.
    /// </summary>
    public void Start() {
        if (disposed || registered || audioManager == null) {
            return;
        }
        try {
            audioManager.RegisterAudioDeviceCallback(deviceCallback, mainHandler);
            registered = true;
            RefreshActiveDevice(applyAutoSwitch: true);
        } catch (Exception ex) {
            AppLogger.Error("AudioDeviceProfileManager.Start", ex);
        }
    }

    /// <summary>
    /// Re-evaluates connected output devices immediately and optionally triggers per-device profile auto-switching.
    /// </summary>
    public void RefreshActiveDevice(bool applyAutoSwitch) {
        if (disposed || audioManager == null) {
            return;
        }
        try {
            AudioDeviceInfo[] outputs = audioManager.GetDevices(GetDevicesTargets.Outputs);
            AudioDeviceInfo[] inputs = audioManager.GetDevices(GetDevicesTargets.Inputs);
            AudioDeviceRouteInfo detected = SelectPrimaryOutputRoute(outputs, inputs);
            bool changed = !string.Equals(detected.DeviceKey, currentDevice.DeviceKey, StringComparison.OrdinalIgnoreCase) ||
                           detected.OutputDeviceId != currentDevice.OutputDeviceId ||
                           detected.InputDeviceId != currentDevice.InputDeviceId;
            currentDevice = detected;

            engine.SceneHub.ActiveAudioDeviceKey = detected.DeviceKey;
            engine.SceneHub.ActiveAudioDeviceName = detected.DisplayName;

            if (changed) {
                AppLogger.Info($"Audio route changed -> '{detected.DisplayName}' [{detected.Transport}] (key='{detected.DeviceKey}')");
            }

            if (applyAutoSwitch && store.AutoEqAutoSwitchByDevice) {
                ApplyBindingForDevice(detected, notifyListeners: true);
            } else if (changed) {
                ActiveDeviceChanged?.Invoke(detected, store.FindDeviceProfile(detected.DeviceKey), false);
            }
        } catch (Exception ex) {
            AppLogger.Error("AudioDeviceProfileManager.RefreshActiveDevice", ex);
        }
    }

    /// <summary>
    /// Saves the currently active AutoEq, Neutral Target, Binaural HRIR, and Head-Tracking preferences as the
    /// remembered profile for <see cref="CurrentDevice"/>.
    /// </summary>
    public void SaveCurrentSettingsForActiveDevice() {
        if (disposed || currentDevice == null) {
            return;
        }
        AudioDeviceProfileBinding existing = store.FindDeviceProfile(currentDevice.DeviceKey);
        AudioDeviceProfileBinding binding = new() {
            DeviceKey = currentDevice.DeviceKey,
            DeviceDisplayName = currentDevice.DisplayName,
            Transport = currentDevice.Transport,
            AutoEqProfileId = store.AutoEqProfileId,
            AutoEqProfileName = store.AutoEqProfileName,
            AutoEqEnabled = store.AutoEqEnabled,
            AutoEqTargetIndex = store.AutoEqTargetIndex,
            BinauralProfileId = BinauralProfileService.GetEffectiveProfileId(store),
            HeadTrackingEnabled = store.HeadTrackingEnabled,
            AutoMatched = existing?.AutoMatched ?? false
        };
        store.UpsertDeviceProfile(binding);
    }

    void OnHardwareDevicesChanged() {
        if (disposed) {
            return;
        }
        RefreshActiveDevice(applyAutoSwitch: true);
    }

    void ApplyBindingForDevice(AudioDeviceRouteInfo device, bool notifyListeners) {
        AudioDeviceProfileBinding saved = store.FindDeviceProfile(device.DeviceKey);
        if (saved != null) {
            ApplyStoredBinding(device, saved, autoMatched: false, notifyListeners);
            return;
        }

        // Built-in phone speaker without an explicit user binding: bypass headphone AutoEq cleanly so speakers aren't colored
        if (!device.IsHeadphoneRoute) {
            engine.AutoEqStage.UserEnabled = false;
            engine.AutoEqStage.ClearProfile();
            engine.SceneHub.ActiveAutoEqProfileId = string.Empty;
            engine.SceneHub.ActiveAutoEqProfileName = string.Empty;
            if (notifyListeners) {
                ActiveDeviceChanged?.Invoke(device, null, false);
            }
            return;
        }

        // Newly connected Bluetooth/USB headphone: try high-confidence automatic AutoEq model lookup
        if (device.Transport == AudioDeviceTransport.BluetoothA2dp ||
            device.Transport == AudioDeviceTransport.BleAudio ||
            device.Transport == AudioDeviceTransport.UsbDac) {
            AutoEqCatalogEntry matched = AutoEqService.MatchDeviceName(context, device.DisplayName);
            if (matched != null) {
                AutoEqHeadphoneProfile offlineProfile = AutoEqService.TryResolveOffline(context, store, matched.Id);
                AudioDeviceProfileBinding autoBinding = new() {
                    DeviceKey = device.DeviceKey,
                    DeviceDisplayName = device.DisplayName,
                    Transport = device.Transport,
                    AutoEqProfileId = matched.Id,
                    AutoEqProfileName = matched.Name,
                    AutoEqEnabled = true,
                    AutoEqTargetIndex = store.AutoEqTargetIndex,
                    BinauralProfileId = BinauralProfileService.GetEffectiveProfileId(store),
                    HeadTrackingEnabled = store.HeadTrackingEnabled,
                    AutoMatched = true
                };
                store.UpsertDeviceProfile(autoBinding);
                store.AutoEqProfileId = matched.Id;
                store.AutoEqProfileName = matched.Name;
                store.AutoEqEnabled = true;

                if (offlineProfile != null) {
                    AutoEqService.ApplyProfileToEngine(store, engine, offlineProfile);
                    AppLogger.Info($"Auto-matched connected device '{device.DisplayName}' -> offline AutoEq '{matched.Name}'.");
                    if (notifyListeners) {
                        ActiveDeviceChanged?.Invoke(device, autoBinding, true);
                    }
                } else {
                    // Catalog match not bundled offline: fetch asynchronously in background & apply seamlessly
                    _ = FetchAndApplyAutoMatchAsync(device, autoBinding);
                }
                return;
            }
        }

        // Unrecognized wired 3.5mm or generic USB DAC: apply user's currently configured AutoEq setting cleanly
        AutoEqService.ApplySavedToEngine(context, store, engine);
        if (notifyListeners) {
            ActiveDeviceChanged?.Invoke(device, null, false);
        }
    }

    void ApplyStoredBinding(AudioDeviceRouteInfo device, AudioDeviceProfileBinding binding, bool autoMatched, bool notifyListeners) {
        // 1. Restore Binaural HRIR / Direct 2.0 profile if bound
        if (!string.IsNullOrWhiteSpace(binding.BinauralProfileId)) {
            string curBinaural = BinauralProfileService.GetEffectiveProfileId(store);
            if (!string.Equals(curBinaural, binding.BinauralProfileId, StringComparison.OrdinalIgnoreCase)) {
                if (BinauralProfileService.ApplyProfile(context, store, binding.BinauralProfileId, out _)) {
                    engine.RequestSmoothTransition();
                    engine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                    BinauralProfileService.SyncToSceneHub(engine.SceneHub, store);
                }
            }
        }

        // 2. Restore Head-Tracking preference (ready for Wave W04 Slot 4 providers)
        store.HeadTrackingEnabled = binding.HeadTrackingEnabled;

        // 3. Restore AutoEq profile & neutral target with Zero Double-EQ Guard
        store.AutoEqTargetIndex = binding.AutoEqTargetIndex;
        store.AutoEqProfileId = binding.AutoEqProfileId ?? string.Empty;
        store.AutoEqProfileName = binding.AutoEqProfileName ?? string.Empty;
        store.AutoEqEnabled = binding.AutoEqEnabled;

        if (binding.AutoEqEnabled && !string.IsNullOrWhiteSpace(binding.AutoEqProfileId)) {
            AutoEqHeadphoneProfile offline = AutoEqService.TryResolveOffline(context, store, binding.AutoEqProfileId);
            if (offline != null) {
                AutoEqService.ApplyProfileToEngine(store, engine, offline);
            } else {
                _ = FetchAndApplyAutoMatchAsync(device, binding);
            }
        } else {
            AutoEqService.ApplyProfileToEngine(store, engine, null);
        }

        if (notifyListeners) {
            ActiveDeviceChanged?.Invoke(device, binding, autoMatched);
        }
    }

    async Task FetchAndApplyAutoMatchAsync(AudioDeviceRouteInfo device, AudioDeviceProfileBinding binding) {
        try {
            AutoEqHeadphoneProfile fetched = await AutoEqService.ResolveProfileAsync(context, store, binding.AutoEqProfileId).ConfigureAwait(false);
            if (disposed || fetched == null) {
                return;
            }
            mainHandler.Post(() => {
                if (disposed || !string.Equals(currentDevice.DeviceKey, device.DeviceKey, StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                AutoEqService.ApplyProfileToEngine(store, engine, fetched);
                ActiveDeviceChanged?.Invoke(device, binding, binding.AutoMatched);
            });
        } catch (Exception ex) {
            AppLogger.Warn($"Background AutoEq fetch failed for '{binding.AutoEqProfileName}': {ex.Message}");
        }
    }

    static AudioDeviceRouteInfo SelectPrimaryOutputRoute(AudioDeviceInfo[] outputs, AudioDeviceInfo[] inputs) {
        if (outputs == null || outputs.Length == 0) {
            return new AudioDeviceRouteInfo();
        }

        AudioDeviceInfo best = null;
        AudioDeviceTransport bestTransport = AudioDeviceTransport.Unknown;
        int bestPriority = -1;

        for (int i = 0; i < outputs.Length; i++) {
            AudioDeviceInfo dev = outputs[i];
            if (dev == null || !dev.IsSink) {
                continue;
            }
            (AudioDeviceTransport transport, int prio) = ClassifyDevice(dev.Type);
            if (prio > bestPriority) {
                bestPriority = prio;
                bestTransport = transport;
                best = dev;
            }
        }

        if (best == null) {
            return new AudioDeviceRouteInfo();
        }

        string rawName = best.ProductName?.ToString()?.Trim() ?? string.Empty;
        string displayName = BuildDisplayName(rawName, bestTransport);
        string key = BuildDeviceKey(rawName, bestTransport);
        int[] rates = Array.Empty<int>();
        try {
            rates = best.GetSampleRates() ?? Array.Empty<int>();
        } catch {
            rates = Array.Empty<int>();
        }

        AudioDeviceInfo bestInput = SelectBestInputDevice(inputs, bestTransport, rawName);

        return new AudioDeviceRouteInfo {
            DeviceKey = key,
            DisplayName = displayName,
            Transport = bestTransport,
            NativeDevice = best,
            NativeInputDevice = bestInput,
            OutputDeviceId = best.Id,
            InputDeviceId = bestInput?.Id ?? 0,
            SupportedSampleRates = rates
        };
    }

    static AudioDeviceInfo SelectBestInputDevice(AudioDeviceInfo[] inputs, AudioDeviceTransport outputTransport, string outputName) {
        if (inputs == null || inputs.Length == 0) {
            return null;
        }
        AudioDeviceInfo best = null;
        int bestScore = -1;
        for (int i = 0; i < inputs.Length; i++) {
            AudioDeviceInfo dev = inputs[i];
            if (dev == null || !dev.IsSource) {
                continue;
            }
            int score = dev.Type switch {
                AudioDeviceType.UsbDevice => 100,
                AudioDeviceType.UsbHeadset => 95,
                AudioDeviceType.WiredHeadset => 70,
                AudioDeviceType.LineAnalog or AudioDeviceType.LineDigital => 65,
                AudioDeviceType.BuiltinMic => 20,
                _ => 5
            };
            if (outputTransport == AudioDeviceTransport.UsbDac &&
                (dev.Type == AudioDeviceType.UsbDevice || dev.Type == AudioDeviceType.UsbHeadset)) {
                score += 40;
                string inName = dev.ProductName?.ToString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(outputName) &&
                    string.Equals(inName, outputName, StringComparison.OrdinalIgnoreCase)) {
                    score += 30;
                }
            }
            if (score > bestScore) {
                bestScore = score;
                best = dev;
            }
        }
        return best;
    }

    static (AudioDeviceTransport Transport, int Priority) ClassifyDevice(AudioDeviceType type) {
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            if (type == AudioDeviceType.BleHeadset) {
                return (AudioDeviceTransport.BleAudio, 100);
            }
            if (type == AudioDeviceType.BleSpeaker) {
                return (AudioDeviceTransport.BleAudio, 88);
            }
        }
        return type switch {
            AudioDeviceType.BluetoothA2dp => (AudioDeviceTransport.BluetoothA2dp, 95),
            AudioDeviceType.UsbHeadset => (AudioDeviceTransport.UsbDac, 90),
            AudioDeviceType.UsbDevice => (AudioDeviceTransport.UsbDac, 85),
            AudioDeviceType.WiredHeadphones => (AudioDeviceTransport.WiredJack, 80),
            AudioDeviceType.WiredHeadset => (AudioDeviceTransport.WiredJack, 78),
            AudioDeviceType.Hdmi => (AudioDeviceTransport.HdmiOrLineOut, 60),
            AudioDeviceType.LineAnalog or AudioDeviceType.LineDigital or AudioDeviceType.AuxLine => (AudioDeviceTransport.HdmiOrLineOut, 55),
            AudioDeviceType.BuiltinSpeaker => (AudioDeviceTransport.BuiltinSpeaker, 10),
            _ => (AudioDeviceTransport.Unknown, -1)
        };
    }

    static string BuildDisplayName(string rawName, AudioDeviceTransport transport) {
        bool genericPhone = string.IsNullOrWhiteSpace(rawName) ||
                            rawName.Equals("Android", StringComparison.OrdinalIgnoreCase);
        return transport switch {
            AudioDeviceTransport.BuiltinSpeaker => "Built-in Speaker",
            AudioDeviceTransport.WiredJack => genericPhone ? "Wired 3.5mm Headphones" : $"{rawName} (3.5mm)",
            AudioDeviceTransport.UsbDac => genericPhone ? "USB-C DAC / Headset" : rawName,
            AudioDeviceTransport.BluetoothA2dp => genericPhone ? "Bluetooth Headphones" : rawName,
            AudioDeviceTransport.BleAudio => genericPhone ? "Bluetooth LE Audio" : rawName,
            AudioDeviceTransport.HdmiOrLineOut => genericPhone ? "HDMI / Line Out" : rawName,
            _ => genericPhone ? "Default Audio Output" : rawName
        };
    }

    static string BuildDeviceKey(string rawName, AudioDeviceTransport transport) {
        if (transport == AudioDeviceTransport.BuiltinSpeaker) {
            return "speaker:builtin";
        }
        if (transport == AudioDeviceTransport.WiredJack) {
            return "wired:35mm_headphones";
        }
        string prefix = transport switch {
            AudioDeviceTransport.BleAudio => "ble",
            AudioDeviceTransport.BluetoothA2dp => "bt",
            AudioDeviceTransport.UsbDac => "usb",
            AudioDeviceTransport.HdmiOrLineOut => "line",
            _ => "out"
        };
        string slug = Slugify(rawName);
        if (string.IsNullOrEmpty(slug)) {
            slug = "default";
        }
        return $"{prefix}:{slug}";
    }

    static string Slugify(string input) {
        if (string.IsNullOrWhiteSpace(input)) {
            return string.Empty;
        }
        StringBuilder sb = new(input.Length);
        bool prevUnderscore = true;
        for (int i = 0; i < input.Length; i++) {
            char c = char.ToLowerInvariant(input[i]);
            if (char.IsLetterOrDigit(c)) {
                sb.Append(c);
                prevUnderscore = false;
            } else if (!prevUnderscore) {
                sb.Append('_');
                prevUnderscore = true;
            }
        }
        return sb.ToString().Trim('_');
    }

    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        if (registered && audioManager != null) {
            try {
                audioManager.UnregisterAudioDeviceCallback(deviceCallback);
            } catch {
                // Ignore
            }
            registered = false;
        }
        GC.SuppressFinalize(this);
    }

    sealed class RouteCallback(Action onChanged) : AudioDeviceCallback {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[] addedDevices) {
            base.OnAudioDevicesAdded(addedDevices);
            onChanged?.Invoke();
        }

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
            base.OnAudioDevicesRemoved(removedDevices);
            onChanged?.Invoke();
        }
    }
}
