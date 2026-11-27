using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Cavern.Channels;
using Cavern.Filters;
using Cavern.Rendering;
using Cavern.Utilities;
using Cavern.Virtualizer;

namespace Cavern {
    /// <summary>
    /// Isolated per-instance rendering context encapsulating speaker layout, room geometry, renderer selection,
    /// and headphone virtualization settings so concurrent playback and offline exports never race.
    /// </summary>
    public sealed class ListenerContext {
        static readonly string[] nonDesktopOsMethods = {
            "IsAndroid", "IsIOS", "IsTvOS", "IsWatchOS", "IsBrowser", "IsWasi"
        };

        /// <summary>
        /// True when running on a desktop platform where user config files under ApplicationData are supported.
        /// </summary>
        internal static readonly bool IsDesktopConfigSupported = !DetectNonDesktopPlatform();

        [ThreadStatic]
        static ListenerContext threadCurrent;

        /// <summary>
        /// Process-wide fallback context preserving 100% legacy compatibility for callers using static <see cref="Listener"/> APIs.
        /// </summary>
        public static ListenerContext Default { get; } = new ListenerContext();

        /// <summary>
        /// Active <see cref="ListenerContext"/> for the calling thread, or <see cref="Default"/> when unscoped.
        /// </summary>
        public static ListenerContext Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => threadCurrent ?? Default;
        }

        /// <summary>
        /// Zero-allocation thread scope restoring the prior <see cref="Current"/> context on disposal.
        /// </summary>
        public struct Scope : IDisposable {
            readonly ListenerContext previous;
            bool disposed;

            internal Scope(ListenerContext previous) {
                this.previous = previous;
                disposed = false;
            }

            /// <summary>
            /// Restores the previous thread-scoped <see cref="ListenerContext"/>.
            /// </summary>
            public void Dispose() {
                if (disposed) {
                    return;
                }
                disposed = true;
                threadCurrent = previous;
            }
        }

        /// <summary>
        /// 3D environment type.
        /// </summary>
        public Environments EnvironmentType {
            get => environmentType;
            set {
                environmentType = value;
                Recalculate();
            }
        }
        Environments environmentType = Environments.Home;

        /// <summary>
        /// Virtual surround effect for headphones.
        /// </summary>
        public bool HeadphoneVirtualizer {
            get => headphoneVirtualizer;
            set {
                headphoneVirtualizer = value;
                Recalculate();
            }
        }
        bool headphoneVirtualizer;

        /// <summary>
        /// Output channel layout. Defaults to standard 5.1.
        /// </summary>
        public Channel[] Channels { get; internal set; }

        /// <summary>
        /// Algorithm used for rendering <see cref="Source"/>s to <see cref="Channels"/>.
        /// </summary>
        public SourceRenderer Renderer { get; internal set; }

        /// <summary>
        /// Gets if the speakers are placed in a sphere according to current context settings.
        /// </summary>
        public bool IsSpherical {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => environmentType == Environments.Studio || headphoneVirtualizer;
        }

        /// <summary>
        /// Whether the speaker layout is symmetrical.
        /// </summary>
        public bool IsSymmetric { get; internal set; } = true;

        /// <summary>
        /// Room scaling dimensions for symmetric rendering.
        /// </summary>
        public Vector3 EnvironmentSize {
            get => environmentSize;
            set {
                environmentSize = value;
                environmentSizeInverse = Vector3.One / value;
            }
        }
        Vector3 environmentSize = new Vector3(10, 7, 10);

        /// <summary>
        /// Cached 1 / <see cref="EnvironmentSize"/>.
        /// </summary>
        public Vector3 EnvironmentSizeInverse {
            get => environmentSizeInverse;
            internal set => environmentSizeInverse = value;
        }
        Vector3 environmentSizeInverse = new Vector3(1 / 10f, 1 / 7f, 1 / 10f);

        /// <summary>
        /// Relative size of the screen to the front wall's width.
        /// </summary>
        public Vector2 ScreenSize { get; set; } = new Vector2(.9f, .486f);

        /// <summary>
        /// Channel count on the left side of the room (minimum 1 for safe division).
        /// </summary>
        public int LeftChannels { get; internal set; } = 2;

        /// <summary>
        /// Channel count on the right side of the room (minimum 1 for safe division).
        /// </summary>
        public int RightChannels { get; internal set; } = 2;

        /// <summary>
        /// Creates a new isolated <see cref="ListenerContext"/> initialized to default 5.1 home room settings.
        /// </summary>
        public ListenerContext() {
            Channels = CreateDefaultChannels();
            Renderer = new BalanceBasedRenderer();
            LayoutChecks();
            Recalculate();
        }

        /// <summary>
        /// Creates a deep copy of <paramref name="source"/> with independent channel instances and renderer.
        /// </summary>
        public ListenerContext(ListenerContext source) {
            ListenerContext src = source ?? Default;
            Channel[] srcChannels = src.Channels;
            if (srcChannels != null) {
                Channel[] cloned = new Channel[srcChannels.Length];
                for (int i = 0; i < srcChannels.Length; i++) {
                    Channel c = srcChannels[i];
                    cloned[i] = new Channel(c.X, c.Y, c.LFE);
                }
                Channels = cloned;
            } else {
                Channels = CreateDefaultChannels();
            }
            environmentType = src.environmentType;
            headphoneVirtualizer = src.headphoneVirtualizer;
            environmentSize = src.environmentSize;
            environmentSizeInverse = src.environmentSizeInverse;
            ScreenSize = src.ScreenSize;
            IsSymmetric = src.IsSymmetric;
            LeftChannels = src.LeftChannels;
            RightChannels = src.RightChannels;
            if (src.Renderer is DirectionalRenderer dir) {
                Renderer = new DirectionalRenderer {
                    UseBs2127Vbap = dir.UseBs2127Vbap
                };
            } else if (src.Renderer is BalanceBasedRenderer) {
                Renderer = new BalanceBasedRenderer();
            } else {
                Renderer = IsSymmetric ? (SourceRenderer)new BalanceBasedRenderer() : new DirectionalRenderer();
            }
            LayoutChecks();
            Recalculate();
        }

        /// <summary>
        /// Creates an independent deep clone of this <see cref="ListenerContext"/>.
        /// </summary>
        public ListenerContext Clone() => new ListenerContext(this);

        /// <summary>
        /// Binds this context as <see cref="Current"/> on the calling thread until the returned scope is disposed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Scope EnterScope() => new Scope(PushThread(this));

        /// <summary>
        /// Current speaker layout name in the format of &lt;main&gt;.&lt;LFE&gt;.&lt;height&gt;.&lt;floor&gt;,
        /// or "Virtualization".
        /// </summary>
        public string GetLayoutName() {
            if (headphoneVirtualizer) {
                return "Virtualization";
            }
            int regular = 0, sub = 0, ceiling = 0, floor = 0;
            for (int channel = 0; channel < Channels.Length; channel++) {
                if (Channels[channel].LFE) {
                    sub++;
                } else if (Channels[channel].X == 0) {
                    regular++;
                } else if (Channels[channel].X < 0) {
                    ceiling++;
                } else if (Channels[channel].X > 0) {
                    floor++;
                }
            }
            StringBuilder layout = new StringBuilder(regular.ToString()).Append('.').Append(sub);
            if (ceiling > 0 || floor > 0) {
                layout.Append('.').Append(ceiling);
            }
            if (floor > 0) {
                layout.Append('.').Append(floor);
            }
            return layout.ToString();
        }

        /// <summary>
        /// Replace the channel layout and update symmetry and renderer for this context.
        /// </summary>
        public void ReplaceChannels(Channel[] channels) {
            Channels = channels ?? Array.Empty<Channel>();
            LayoutChecks();
            Recalculate();
            Renderer = IsSymmetric ? (SourceRenderer)
                new BalanceBasedRenderer() :
                new DirectionalRenderer();
        }

        /// <summary>
        /// Replace the channel layout with a standard layout of <paramref name="channelCount"/> channels.
        /// </summary>
        public void ReplaceChannels(int channelCount) =>
            ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.GetStandardMatrix(channelCount)));

        /// <summary>
        /// Recalculate channel positions for the current spherical/cubical room shape.
        /// </summary>
        internal void Recalculate() {
            if (Channels == null) {
                return;
            }
            bool spherical = IsSpherical;
            for (int channel = 0; channel < Channels.Length; channel++) {
                Channels[channel].Recalculate(spherical);
            }
        }

        /// <summary>
        /// Recalculate layout symmetry and side channel counts.
        /// </summary>
        internal void LayoutChecks() {
            if (Channels == null) {
                return;
            }
            IsSymmetric = SymmetryCalculator.CalculateSymmetry(Channels);
            (LeftChannels, RightChannels) = ChannelArrayExtensions.GetSideChannels(Channels);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ListenerContext PushThread(ListenerContext context) {
            ListenerContext prev = threadCurrent;
            threadCurrent = context ?? Default;
            return prev;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void PopThread(ListenerContext previous) => threadCurrent = previous;

        static Channel[] CreateDefaultChannels() => new Channel[] {
            new Channel(0, -30), new Channel(0, 30),
            new Channel(0, 0), new Channel(15, 15, true), new Channel(0, -110), new Channel(0, 110)
        };

        static bool DetectNonDesktopPlatform() {
            try {
                Type osType = typeof(Environment).Assembly.GetType("System.OperatingSystem");
                if (osType != null) {
                    for (int i = 0; i < nonDesktopOsMethods.Length; i++) {
                        MethodInfo method = osType.GetMethod(nonDesktopOsMethods[i], BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        if (method != null && method.Invoke(null, null) is bool isMobile && isMobile) {
                            return true;
                        }
                    }
                }
                if (Type.GetType("Android.OS.Build, Mono.Android", throwOnError: false) != null) {
                    return true;
                }
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_ROOT")) ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_DATA"))) {
                    return true;
                }
                string osDesc = RuntimeInformation.OSDescription;
                if (!string.IsNullOrEmpty(osDesc) && osDesc.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
                if (string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))) {
                    return true;
                }
            } catch {
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Center of a listening space. Attached <see cref="Source"/>s will be rendered relative to this object's position.
    /// </summary>
    public partial class Listener {
        /// <summary>
        /// Center of a listening space. Attached <see cref="Source"/>s will be rendered relative to this object's position.
        /// The layout set up by the user will be used.
        /// </summary>
        public Listener() : this(ListenerContext.Current, true) { }

        /// <summary>
        /// Center of a listening space. Attached <see cref="Source"/>s will be rendered relative to this object's position.
        /// </summary>
        /// <param name="loadGlobals">Load the global settings for all listeners. This should be false for listeners created
        /// on the fly, as this overwrites previous application settings that might have been modified.</param>
        public Listener(bool loadGlobals) : this(ListenerContext.Current, loadGlobals) { }

        /// <summary>
        /// Center of a listening space bound to an explicit <see cref="ListenerContext"/>.
        /// </summary>
        /// <param name="context">Isolated rendering context for this listener instance</param>
        /// <param name="loadGlobals">Load desktop global settings into <paramref name="context"/> when supported</param>
        public Listener(ListenerContext context, bool loadGlobals = false) {
            this.context = context ?? ListenerContext.Default;
            if (!loadGlobals || !ListenerContext.IsDesktopConfigSupported) {
                return;
            }
            string fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cavern", "Save.dat");
            if (File.Exists(fileName)) {
                string[] save = File.ReadAllLines(fileName);
                try {
                    int savePos = 1;
                    Channel[] loaded = new Channel[Convert.ToInt32(save[0])];
                    for (int i = 0; i < loaded.Length; i++) {
                        loaded[i] = new Channel(
                            QMath.ParseFloat(save[savePos++]),
                            QMath.ParseFloat(save[savePos++]),
                            Convert.ToBoolean(save[savePos++])
                        );
                    }
                    this.context.ReplaceChannels(loaded);
                    this.context.EnvironmentType = (Environments)Convert.ToInt32(save[savePos++]);
                    this.context.EnvironmentSize = new Vector3(
                        QMath.ParseFloat(save[savePos++]),
                        QMath.ParseFloat(save[savePos++]),
                        QMath.ParseFloat(save[savePos++])
                    );
                    this.context.HeadphoneVirtualizer = save.Length > savePos && Convert.ToBoolean(save[savePos++]); // Added: 2016.04.24.
                    savePos++; // Environment compensation (bool), added: 2017.06.18, removed: 2019.06.06.
                } catch {
                    this.context.Channels = ChannelPrototype.ToLayout(ChannelPrototype.GetStandardMatrix(6));
                    this.context.IsSymmetric = true;
                    this.context.LeftChannels = 2;
                    this.context.RightChannels = 2;
                    this.context.EnvironmentType = Environments.Home;
                    this.context.EnvironmentSize = new Vector3(10, 7, 10);
                }
            }
        }

        /// <summary>
        /// Current speaker layout name in the format of &lt;main&gt;.&lt;LFE&gt;.&lt;height&gt;.&lt;floor&gt;,
        /// or simply "Virtualization".
        /// </summary>
        public static string GetLayoutName() => ListenerContext.Current.GetLayoutName();

        /// <summary>
        /// Replace the channel layout.
        /// </summary>
        /// <remarks>If you're making your own configurator, don't forget to overwrite the Cavern configuration file.</remarks>
        public static void ReplaceChannels(Channel[] channels) => ListenerContext.Current.ReplaceChannels(channels);

        /// <summary>
        /// Replace the channel layout with a standard of a given channel count.
        /// The <see cref="Listener"/> will set up itself automatically with the user's saved configuration.
        /// The used audio channels can be queried through <see cref="Channels"/>, which should be respected,
        /// and the output audio channel count should be set to its length. If this is not possible,
        /// the layout could be set to a standard by the number of channels with this function.
        /// </summary>
        public static void ReplaceChannels(int channelCount) => ListenerContext.Current.ReplaceChannels(channelCount);

        /// <summary>
        /// Implicit null check.
        /// </summary>
        public static implicit operator bool(Listener listener) => listener != null;

        /// <summary>
        /// Recalculate the rendering environment.
        /// </summary>
        static void Recalculate() => ListenerContext.Current.Recalculate();

        /// <summary>
        /// Recalculates values related to the <see cref="Channels"/> when anything in the layout had changed.
        /// </summary>
        static void LayoutChecks() => ListenerContext.Current.LayoutChecks();

        /// <summary>
        /// Attach a source to this listener.
        /// </summary>
        public void AttachSource(Source source) {
            if (source.listener) {
                source.listener.DetachSource(source);
            }
            source.listenerNode = activeSources.AddLast(source);
            source.listener = this;
        }

        /// <summary>
        /// Attach multiple sources to this listener.
        /// </summary>
        public void AttachSources(IEnumerable<Source> sources) {
            foreach (Source source in sources) {
                AttachSource(source);
            }
        }

        /// <summary>
        /// Attach a source to this listener, to the first place of the processing queue.
        /// </summary>
        public void AttachPrioritySource(Source source) {
            if (source.listener) {
                source.listener.DetachSource(source);
            }
            source.listenerNode = activeSources.AddFirst(source);
            source.listener = this;
        }

        /// <summary>
        /// Detach a source from this listener.
        /// </summary>
        public void DetachSource(Source source) {
            if (source == this) {
                activeSources.Remove(source.listenerNode);
                source.listener = null;
            }
        }

        /// <summary>
        /// Detach all sources from this listener.
        /// </summary>
        public void DetachAllSources() {
            for (int i = 0, c = activeSources.Count; i < c; i++) {
                activeSources.First.Value.listener = null;
                activeSources.RemoveFirst();
            }
        }

        /// <summary>
        /// Perform an update on all objects without rendering anything to the listener's output.
        /// </summary>
        public void Ping() {
            ListenerContext prev = ListenerContext.PushThread(Context);
            try {
                LinkedListNode<Source> node = activeSources.First;
                while (node != null) {
                    sourceDistances[0] = Range;
                    node.Value.Precalculate();
                    node.Value.Precollect();
                    node = node.Next;
                }
            } finally {
                ListenerContext.PopThread(prev);
            }
        }

        /// <summary>
        /// Ask for update ticks for a single frame.
        /// </summary>
        /// <remarks>The output size is <see cref="UpdateRate"/> * <see cref="Channels"/>.Length.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float[] Render() => Render(1);

        /// <summary>
        /// Ask for update ticks for multiple frames.
        /// </summary>
        /// <remarks>The output size is <paramref name="frames"/> * <see cref="UpdateRate"/> * <see cref="Channels"/>.Length.</remarks>
        public virtual float[] Render(int frames) {
            if (SampleRate < 8000 || UpdateRate < 16) { // Don't work with wrong settings
                return null;
            }
            ListenerContext ctx = Context;
            ListenerContext prev = ListenerContext.PushThread(ctx);
            try {
                for (int source = 0; source < sourceDistances.Length; source++) {
                    sourceDistances[source] = Range;
                }
                pulseDelta = frames * UpdateRate / (float)SampleRate;

                // Choose the sources to play
                LinkedListNode<Source> node = activeSources.First;
                while (node != null) {
                    node.Value.Precalculate();
                    node = node.Next;
                }

                // Render the required number of frames
                if (frames == 1) {
                    float[] result = Frame(ctx);
                    if (ctx.HeadphoneVirtualizer && !BypassInternalVirtualizer) {
                        virtualizer.Process(result, SampleRate);
                    }
                    return result;
                } else {
                    int channelLength = ctx.Channels.Length;
                    try {
                        int sampleCount = frames * channelLength * UpdateRate;
                        if (multiframeBuffer.Length != sampleCount) {
                            multiframeBuffer = new float[sampleCount];
                        }
                    } catch (OverflowException) {
                        throw new OverflowException($"Total sample count has overflown with {frames} frames * {channelLength} channels * {UpdateRate} samples per update.");
                    }

                    for (int frame = 0; frame < frames; frame++) {
                        float[] frameBuffer = Frame(ctx);
                        Array.Copy(frameBuffer, 0, multiframeBuffer, frame * frameBuffer.Length, frameBuffer.Length);
                    }
                    if (ctx.HeadphoneVirtualizer && !BypassInternalVirtualizer) {
                        virtualizer.Process(multiframeBuffer, SampleRate);
                    }
                    return multiframeBuffer;
                }
            } finally {
                ListenerContext.PopThread(prev);
            }
        }

        /// <summary>
        /// Recreate optimization arrays.
        /// </summary>
        void Reoptimize(int targetChannelCount) {
            channelCount = targetChannelCount;
            lastSampleRate = SampleRate;
            lastUpdateRate = UpdateRate;
            renderBuffer = new float[channelCount * UpdateRate];
            lowpasses = new Lowpass[channelCount];
            for (int i = 0; i < channelCount; i++) {
                lowpasses[i] = new Lowpass(SampleRate, 120);
            }
            fallbackLfeLowpass = new Lowpass(SampleRate, 120);
        }

        /// <summary>
        /// A single update.
        /// </summary>
        float[] Frame(ListenerContext ctx) {
            if (ctx.HeadphoneVirtualizer && !BypassInternalVirtualizer) {
                virtualizer ??= new VirtualizerFilter();
                virtualizer.SetLayout();
            }
            Channel[] ctxChannels = ctx.Channels;
            if (channelCount != ctxChannels.Length || lastSampleRate != SampleRate || lastUpdateRate != UpdateRate) {
                Reoptimize(ctxChannels.Length);
            }

            // Collect audio data from sources
            LinkedListNode<Source> node = activeSources.First;
            results.Clear();
            while (node != null) {
                if (node.Value.Precollect()) {
                    results.Add(node.Value.Collect());
                }
                node = node.Next;
            }

            // Mix sources to output
            Array.Clear(renderBuffer, 0, renderBuffer.Length);
            for (int result = 0; result < results.Count; result++) {
                WaveformUtils.Mix(results[result], renderBuffer);
            }

            // Volume and subwoofers' lowpass
            for (int channel = 0; channel < channelCount; channel++) {
                if (ctxChannels[channel].LFE) {
                    if (!DirectLFE) {
                        lowpasses[channel].Process(renderBuffer, channel, channelCount);
                    }
                    WaveformUtils.Gain(renderBuffer, LFEVolume * Volume, channel, channelCount); // LFE Volume
                } else {
                    WaveformUtils.Gain(renderBuffer, Volume, channel, channelCount);
                }
            }
            if (Normalizer != 0) { // Normalize
                normalizer.SampleRate = SampleRate;
                normalizer.Channels = channelCount;
                normalizer.decayFactor = Normalizer * UpdateRate / SampleRate;
                normalizer.Process(renderBuffer);
            }
            return renderBuffer;
        }
    }
}
