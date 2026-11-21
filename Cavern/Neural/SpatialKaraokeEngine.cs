using System;

using Cavern.Utilities;

namespace Cavern.Neural {
    /// <summary>
    /// Zero-phase multichannel coherence Wiener Spatial Karaoke engine (Slot 2B, strict C# 8.0, zero-GC in streaming mode).
    /// Projects a single real-valued zero-phase vocal attenuation mask across all 1..32 Atmos bed &amp; dynamic object channels
    /// simultaneously — weighted per channel by each channel's cross-spectral coherence with the reference vocal spectrum
    /// (from an ONNX Lead-vs-Backing neural vocal stem, a synced W09 lossless FLAC twin, or real-time center-coherent estimation).
    /// <para>
    /// Guarantees:
    /// <list type="bullet">
    ///   <item><b>0.000° inter-channel phase distortion</b> across all Atmos beds and 3D objects (strictly real-valued positive mask <c>G_c(t,f) in [0, 1]</c>).</item>
    ///   <item><b>Automatic 3D vocal-reverb removal</b> across surround and height channels wherever energy is coherent with the lead vocal.</item>
    ///   <item><b>100% LFE preservation</b> (subwoofer .1 channels always pass bit-untouched).</item>
    ///   <item><b>Bit-exact unity bypass</b> when <see cref="VocalLevel"/> is <c>1.0</c> (100% vocals) or <see cref="IsEnabled"/> is false.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class SpatialKaraokeEngine : IDisposable {
        /// <summary>Streaming STFT window length (512 samples = 10.67 ms at 48 kHz, matching Cavern's real-time update rate).</summary>
        public const int FftSize = 512;

        /// <summary>Streaming STFT hop size (256 samples = 50% overlap, exact COLA with sqrt(Hann)).</summary>
        public const int HopSize = 256;

        /// <summary>Maximum simultaneous multichannel Atmos beds + 3D objects supported in zero-GC streaming mode.</summary>
        public const int MaxSpatialChannels = 32;

        const int SpectrumBins = (FftSize >> 1) + 1;
        const float Epsilon = 1e-12f;

        readonly int sampleRate;
        readonly ThreadSafeFFTCache fftCache;
        readonly float[] window = new float[FftSize];
        readonly float[] binHz = new float[SpectrumBins];
        readonly float[] vocalBandWeight = new float[SpectrumBins];
        readonly float[] smoothAlpha = new float[SpectrumBins];

        // Streaming ring buffers per channel (zero-GC pre-allocated)
        readonly float[][] prevIn;
        readonly float[][] currIn;
        readonly float[][] olaTail;
        readonly float[][] readyOut;
        readonly Complex[][] chSpec;

        // Reference vocal stream buffers (from neural Lead Vocal stem or W09 lossless twin)
        readonly float[] prevRefLead = new float[HopSize];
        readonly float[] currRefLead = new float[HopSize];
        readonly float[] prevRefBacking = new float[HopSize];
        readonly float[] currRefBacking = new float[HopSize];
        readonly Complex[] refLeadSpec = new Complex[FftSize];
        readonly Complex[] refBackingSpec = new Complex[FftSize];

        // Per-channel cross-spectral covariance accumulators against the reference vocal spectrum
        readonly float[][] covCC;
        readonly float[][] covCVRe;
        readonly float[][] covCVIm;
        readonly float[] covVV = new float[SpectrumBins];
        readonly float[] covBB = new float[SpectrumBins];

        int configuredChannels;
        int inFill;
        bool hasPrimedHop;
        bool wasBypassed = true;
        float bypassFadeMix;
        bool isEnabled;
        float vocalLevel = 1f;
        bool preserveBackingVocals = true;
        float latestVocalActivity;

        /// <summary>Enables or bypasses Spatial Karaoke processing.</summary>
        public bool IsEnabled {
            get { return isEnabled; }
            set { isEnabled = value; }
        }

        /// <summary>
        /// Target lead vocal gain in [0.0; 1.0] (0.0 = full instrumental karaoke, 1.0 = bit-exact unity bypass).
        /// </summary>
        public float VocalLevel {
            get { return vocalLevel; }
            set {
                if (value < 0f) {
                    vocalLevel = 0f;
                } else if (value > 1f) {
                    vocalLevel = 1f;
                } else {
                    vocalLevel = value;
                }
            }
        }

        /// <summary>
        /// When true, protects backing vocals &amp; harmonies using the neural backing reference (or side-channel energy).
        /// </summary>
        public bool PreserveBackingVocals {
            get { return preserveBackingVocals; }
            set { preserveBackingVocals = value; }
        }

        /// <summary>
        /// Live vocal activity probability in [0; 1], exported to <c>SoundfieldTelemetry</c> and W07 <c>SpatialDialogEnhancer</c>.
        /// </summary>
        public float LatestVocalActivity {
            get { return latestVocalActivity; }
        }

        /// <summary>Sample rate configured for this engine.</summary>
        public int SampleRate {
            get { return sampleRate; }
        }

        /// <summary>Creates a zero-GC multichannel Spatial Karaoke engine for <paramref name="sampleRate"/>.</summary>
        public SpatialKaraokeEngine(int sampleRate, int initialChannels = 16) {
            this.sampleRate = sampleRate >= 8000 ? sampleRate : 48000;
            configuredChannels = ClampChannels(initialChannels);
            fftCache = new ThreadSafeFFTCache(FftSize);

            prevIn = new float[MaxSpatialChannels][];
            currIn = new float[MaxSpatialChannels][];
            olaTail = new float[MaxSpatialChannels][];
            readyOut = new float[MaxSpatialChannels][];
            chSpec = new Complex[MaxSpatialChannels][];
            covCC = new float[MaxSpatialChannels][];
            covCVRe = new float[MaxSpatialChannels][];
            covCVIm = new float[MaxSpatialChannels][];

            for (int c = 0; c < MaxSpatialChannels; c++) {
                prevIn[c] = new float[HopSize];
                currIn[c] = new float[HopSize];
                olaTail[c] = new float[HopSize];
                readyOut[c] = new float[HopSize];
                chSpec[c] = new Complex[FftSize];
                covCC[c] = new float[SpectrumBins];
                covCVRe[c] = new float[SpectrumBins];
                covCVIm[c] = new float[SpectrumBins];
            }

            // Exact COLA sqrt(Hann) sine window: w[n]^2 + w[n + N/2]^2 == 1.0 identically
            for (int n = 0; n < FftSize; n++) {
                window[n] = MathF.Sin(MathF.PI * (n + 0.5f) / FftSize);
            }

            float df = this.sampleRate / (float)FftSize;
            float hopSec = HopSize / (float)this.sampleRate;
            for (int k = 0; k < SpectrumBins; k++) {
                float f = k * df;
                binHz[k] = f;
                // Smooth physiological human vocal envelope weight (used only in blind coherence fallback when no neural stem is loaded):
                // protects sub-bass < 110 Hz and extreme ultrasonic air > 12 kHz while covering full vocal formants + sibilance.
                vocalBandWeight[k] = ComputeVocalBandEnvelope(f);
                // Smooth covariance time constant (~42 ms) for stable coherence without musical noise
                smoothAlpha[k] = 1f - MathF.Exp(-hopSec / 0.042f);
            }
        }

        /// <summary>Clears all overlap-add tails and cross-spectral coherence accumulators.</summary>
        public void Reset() {
            for (int c = 0; c < MaxSpatialChannels; c++) {
                Array.Clear(prevIn[c], 0, HopSize);
                Array.Clear(currIn[c], 0, HopSize);
                Array.Clear(olaTail[c], 0, HopSize);
                Array.Clear(readyOut[c], 0, HopSize);
                Array.Clear(covCC[c], 0, SpectrumBins);
                Array.Clear(covCVRe[c], 0, SpectrumBins);
                Array.Clear(covCVIm[c], 0, SpectrumBins);
            }
            Array.Clear(prevRefLead, 0, HopSize);
            Array.Clear(currRefLead, 0, HopSize);
            Array.Clear(prevRefBacking, 0, HopSize);
            Array.Clear(currRefBacking, 0, HopSize);
            Array.Clear(covVV, 0, SpectrumBins);
            Array.Clear(covBB, 0, SpectrumBins);
            inFill = 0;
            hasPrimedHop = false;
            wasBypassed = true;
            bypassFadeMix = 0f;
            latestVocalActivity = 0f;
        }

        /// <summary>
        /// Processes <paramref name="channelCount"/> multichannel Atmos beds/objects in place using zero-phase
        /// multichannel coherence Wiener masking.
        /// </summary>
        /// <param name="channels">Array of per-channel mono sample buffers (<c>[ch][sample]</c>).</param>
        /// <param name="isLfe">Optional LFE channel flags; flagged channels always bypass bit-identically.</param>
        /// <param name="isCenterOrFront">Optional front/center flags used when operating without an external neural vocal stem.</param>
        /// <param name="channelCount">Number of active spatial channels in <paramref name="channels"/> (1..32).</param>
        /// <param name="sampleCount">Number of samples per channel to process.</param>
        /// <param name="neuralLeadVocalRef">Optional sample-aligned mono Lead Vocal stem (from ONNX Karaoke/RoFormer or W09 lossless twin).</param>
        /// <param name="neuralBackingVocalRef">Optional sample-aligned mono Backing Vocal stem (preserved when <see cref="PreserveBackingVocals"/> is true).</param>
        /// <param name="isFullMixTwinReference">True when <paramref name="neuralLeadVocalRef"/> is a non-neural full-mix twin center sum rather than an isolated vocal stem, restricting attenuation to the physiological vocal band.</param>
        public void ProcessMultichannelInPlace(
            float[][] channels,
            bool[] isLfe,
            bool[] isCenterOrFront,
            int channelCount,
            int sampleCount,
            float[] neuralLeadVocalRef = null,
            float[] neuralBackingVocalRef = null,
            bool isFullMixTwinReference = false) {
            if (channels == null || channelCount <= 0 || sampleCount <= 0) {
                return;
            }

            bool targetActive = isEnabled && vocalLevel < 0.9999f;
            // Bit-exact unity bypass once smoothly ramped down to 0
            if (!targetActive && bypassFadeMix <= 0.0001f) {
                bypassFadeMix = 0f;
                wasBypassed = true;
                latestVocalActivity = 0f;
                return;
            }

            int chCount = ClampChannels(channelCount);
            if (chCount > channels.Length) {
                chCount = channels.Length;
            }
            configuredChannels = chCount;

            if (wasBypassed && targetActive) {
                for (int c = 0; c < chCount; c++) {
                    Array.Clear(prevIn[c], 0, HopSize);
                    Array.Clear(currIn[c], 0, HopSize);
                    Array.Clear(olaTail[c], 0, HopSize);
                    Array.Clear(readyOut[c], 0, HopSize);
                }
                Array.Clear(prevRefLead, 0, HopSize);
                Array.Clear(currRefLead, 0, HopSize);
                Array.Clear(prevRefBacking, 0, HopSize);
                Array.Clear(currRefBacking, 0, HopSize);
                inFill = 0;
                hasPrimedHop = false;
                bypassFadeMix = 0f;
                wasBypassed = false;
            }

            bool hasNeuralLead = neuralLeadVocalRef != null && neuralLeadVocalRef.Length >= sampleCount;
            bool hasNeuralBacking = neuralBackingVocalRef != null && neuralBackingVocalRef.Length >= sampleCount;
            const float fadeStep = 1f / HopSize;

            for (int n = 0; n < sampleCount; n++) {
                float smoothMix = 0f;
                if (hasPrimedHop) {
                    bypassFadeMix = targetActive
                        ? Math.Min(1f, bypassFadeMix + fadeStep)
                        : Math.Max(0f, bypassFadeMix - fadeStep);
                    smoothMix = bypassFadeMix * bypassFadeMix * (3f - 2f * bypassFadeMix);
                }

                // Emit smoothly crossfaded COLA hop samples (skipping LFE channels untouched)
                for (int c = 0; c < chCount; c++) {
                    if (channels[c] == null || (isLfe != null && c < isLfe.Length && isLfe[c])) {
                        continue;
                    }
                    float inSample = channels[c][n];
                    currIn[c][inFill] = inSample;
                    if (hasPrimedHop) {
                        channels[c][n] = inSample + (readyOut[c][inFill] - inSample) * smoothMix;
                    }
                }

                currRefLead[inFill] = hasNeuralLead ? neuralLeadVocalRef[n] : 0f;
                currRefBacking[inFill] = hasNeuralBacking ? neuralBackingVocalRef[n] : 0f;
                inFill++;

                if (inFill == HopSize) {
                    ExecuteMultichannelHop(chCount, isLfe, isCenterOrFront, hasNeuralLead, hasNeuralBacking, isFullMixTwinReference);
                    FinishHop(chCount);
                    hasPrimedHop = true;
                }
            }

            if (!targetActive && bypassFadeMix <= 0.0001f) {
                bypassFadeMix = 0f;
                wasBypassed = true;
                latestVocalActivity = 0f;
            }
        }

        /// <summary>
        /// Offline zero-latency multichannel Spatial Karaoke pass (zero delay offset) for Atmos/multichannel export.
        /// Applies the exact same zero-phase multichannel coherence Wiener projection across all beds/objects using
        /// a pre-separated neural Lead Vocal stem (and optional Backing Vocal stem) at 1x neural cost.
        /// </summary>
        public static float[][] ProcessOfflineMultichannel(
            float[][] channels,
            bool[] isLfe,
            float[] neuralLeadVocalRef,
            float[] neuralBackingVocalRef,
            int sampleRate,
            float vocalLevel,
            bool preserveBackingVocals = true,
            int fftSize = 2048) {
            if (channels == null || channels.Length == 0) {
                return Array.Empty<float[]>();
            }
            int chCount = channels.Length;
            int length = channels[0].Length;
            float[][] output = new float[chCount][];
            for (int c = 0; c < chCount; c++) {
                output[c] = new float[length];
                if (vocalLevel >= 0.9999f || (isLfe != null && c < isLfe.Length && isLfe[c])) {
                    Array.Copy(channels[c], output[c], length);
                }
            }
            if (vocalLevel >= 0.9999f || length == 0) {
                return output;
            }
            if (vocalLevel < 0f) {
                vocalLevel = 0f;
            }

            int nFft = fftSize < 512 ? 512 : (fftSize > 4096 ? 4096 : fftSize);
            int hop = nFft >> 1;
            int bins = (nFft >> 1) + 1;
            float[] win = new float[nFft];
            for (int i = 0; i < nFft; i++) {
                win[i] = MathF.Sin(MathF.PI * (i + 0.5f) / nFft);
            }

            int effectiveSr = sampleRate > 0 ? sampleRate : 48000;
            float hopSec = hop / (float)effectiveSr;
            float alpha = 1f - MathF.Exp(-hopSec / 0.045f);
            bool hasLead = neuralLeadVocalRef != null && neuralLeadVocalRef.Length >= length;
            bool hasBacking = preserveBackingVocals && neuralBackingVocalRef != null && neuralBackingVocalRef.Length >= length;
            float[] offlineBandWeight = new float[bins];
            float offlineDf = effectiveSr / (float)nFft;
            for (int k = 0; k < bins; k++) {
                offlineBandWeight[k] = ComputeVocalBandEnvelope(k * offlineDf);
            }

            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(nFft);
            try {
                Complex[][] cSpecs = new Complex[chCount][];
                float[][] cCovCC = new float[chCount][];
                float[][] cCovCVRe = new float[chCount][];
                float[][] cCovCVIm = new float[chCount][];
                for (int c = 0; c < chCount; c++) {
                    cSpecs[c] = new Complex[nFft];
                    cCovCC[c] = new float[bins];
                    cCovCVRe[c] = new float[bins];
                    cCovCVIm[c] = new float[bins];
                }
                Complex[] leadSpec = new Complex[nFft];
                Complex[] backSpec = new Complex[nFft];
                float[] cCovVV = new float[bins];
                float[] cCovBB = new float[bins];
                float[] weightSum = new float[length];

                int numFrames = (length + hop - 1) / hop + 2;
                float attenStrength = 1f - vocalLevel;

                for (int frame = -1; frame < numFrames; frame++) {
                    int start = frame * hop;
                    if (start >= length || start + nFft <= 0) {
                        continue;
                    }

                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        FillFrame(channels[c], start, nFft, win, cSpecs[c]);
                        cSpecs[c].InPlaceFFT(fft);
                    }

                    if (hasLead) {
                        FillFrame(neuralLeadVocalRef, start, nFft, win, leadSpec);
                        leadSpec.InPlaceFFT(fft);
                    }
                    if (hasBacking) {
                        FillFrame(neuralBackingVocalRef, start, nFft, win, backSpec);
                        backSpec.InPlaceFFT(fft);
                    }

                    for (int k = 0; k < bins; k++) {
                        Complex vRef;
                        if (hasLead) {
                            vRef = leadSpec[k];
                        } else {
                            // Blind multichannel center-coherent reference fallback
                            float sumRe = 0f;
                            float sumIm = 0f;
                            int active = 0;
                            for (int c = 0; c < chCount; c++) {
                                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                    continue;
                                }
                                sumRe += cSpecs[c][k].Real;
                                sumIm += cSpecs[c][k].Imaginary;
                                active++;
                            }
                            float invA = active > 0 ? (1f / active) : 1f;
                            vRef = new Complex(sumRe * invA, sumIm * invA);
                        }

                        float vv = vRef.Real * vRef.Real + vRef.Imaginary * vRef.Imaginary;
                        cCovVV[k] += alpha * (vv - cCovVV[k]);

                        float bb = 0f;
                        if (hasBacking) {
                            bb = backSpec[k].Real * backSpec[k].Real + backSpec[k].Imaginary * backSpec[k].Imaginary;
                            cCovBB[k] += alpha * (bb - cCovBB[k]);
                        }

                        // Backing-vocal protection factor: 1.0 when pure lead vocal, drops toward 0 where backing vocal dominates
                        float leadPurity = hasBacking ? (cCovVV[k] / (cCovVV[k] + cCovBB[k] + Epsilon)) : 1f;

                        for (int c = 0; c < chCount; c++) {
                            if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                continue;
                            }
                            Complex x = cSpecs[c][k];
                            float cc = x.Real * x.Real + x.Imaginary * x.Imaginary;
                            float cvRe = x.Real * vRef.Real + x.Imaginary * vRef.Imaginary;
                            float cvIm = x.Imaginary * vRef.Real - x.Real * vRef.Imaginary;

                            cCovCC[c][k] += alpha * (cc - cCovCC[c][k]);
                            cCovCVRe[c][k] += alpha * (cvRe - cCovCVRe[c][k]);
                            cCovCVIm[c][k] += alpha * (cvIm - cCovCVIm[c][k]);

                            float denom = cCovCC[c][k] * cCovVV[k] + Epsilon;
                            float cohSq = (cCovCVRe[c][k] * cCovCVRe[c][k] + cCovCVIm[c][k] * cCovCVIm[c][k]) / denom;
                            if (cohSq > 1f) {
                                cohSq = 1f;
                            }

                            // Neural amplitude-ratio Wiener vocal dominance in channel c
                            float vocalDominance = hasLead
                                ? MathF.Min(1f, cCovVV[k] / (cCovCC[c][k] + Epsilon))
                                : 1f;
                            float offlineBandW = hasLead ? 1f : offlineBandWeight[k];

                            // Strictly real positive gain in [0, 1] -> exact 0.000° phase preservation across all channels!
                            float vocalMask = MathF.Sqrt(cohSq * vocalDominance) * leadPurity * offlineBandW;
                            if (vocalMask > 1f) {
                                vocalMask = 1f;
                            }
                            float gain = 1f - attenStrength * vocalMask;
                            if (gain < 0f) {
                                gain = 0f;
                            }

                            Complex y = new Complex(x.Real * gain, x.Imaginary * gain);
                            cSpecs[c][k] = y;
                            if (k > 0 && k < bins - 1) {
                                cSpecs[c][nFft - k] = new Complex(y.Real, -y.Imaginary);
                            }
                        }
                    }

                    bool firstAccum = true;
                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        cSpecs[c].InPlaceIFFT(fft);
                        for (int n = 0; n < nFft; n++) {
                            int idx = start + n;
                            if (idx >= 0 && idx < length) {
                                float w = win[n];
                                output[c][idx] += cSpecs[c][n].Real * w;
                                if (firstAccum) {
                                    weightSum[idx] += w * w;
                                }
                            }
                        }
                        firstAccum = false;
                    }
                }

                for (int n = 0; n < length; n++) {
                    float w = weightSum[n];
                    float invW = w > Epsilon ? (1f / w) : 1f;
                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        output[c][n] *= invW;
                    }
                }
            } finally {
                fft.Dispose();
            }

            return output;
        }

        void ExecuteMultichannelHop(
            int chCount,
            bool[] isLfe,
            bool[] isCenterOrFront,
            bool hasNeuralLead,
            bool hasNeuralBacking,
            bool isFullMixTwinReference) {
            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                for (int n = 0; n < HopSize; n++) {
                    chSpec[c][n] = new Complex(prevIn[c][n] * window[n], 0f);
                    chSpec[c][n + HopSize] = new Complex(currIn[c][n] * window[n + HopSize], 0f);
                }
                chSpec[c].InPlaceFFT(fftCache);
            }

            if (hasNeuralLead) {
                for (int n = 0; n < HopSize; n++) {
                    refLeadSpec[n] = new Complex(prevRefLead[n] * window[n], 0f);
                    refLeadSpec[n + HopSize] = new Complex(currRefLead[n] * window[n + HopSize], 0f);
                }
                refLeadSpec.InPlaceFFT(fftCache);
            }
            if (hasNeuralBacking) {
                for (int n = 0; n < HopSize; n++) {
                    refBackingSpec[n] = new Complex(prevRefBacking[n] * window[n], 0f);
                    refBackingSpec[n + HopSize] = new Complex(currRefBacking[n] * window[n + HopSize], 0f);
                }
                refBackingSpec.InPlaceFFT(fftCache);
            }

            bool useIsolatedNeuralLead = hasNeuralLead && !isFullMixTwinReference;
            float attenStrength = isEnabled ? (1f - vocalLevel) : 0f;
            double vocalEnergySum = 0.0;
            double totalEnergySum = 0.0;

            for (int k = 0; k < SpectrumBins; k++) {
                float a = smoothAlpha[k];
                Complex vRef;
                float bandW;

                if (hasNeuralLead) {
                    vRef = refLeadSpec[k];
                    bandW = useIsolatedNeuralLead ? 1f : vocalBandWeight[k];
                } else {
                    // Real-time center-coherent vocal reference when no offline neural stem is attached yet:
                    // uses front/center channels (or L+R Mid sum on stereo) weighted by physiological vocal envelope.
                    float sumRe = 0f;
                    float sumIm = 0f;
                    int refCount = 0;
                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        if (isCenterOrFront != null && c < isCenterOrFront.Length && !isCenterOrFront[c]) {
                            continue;
                        }
                        sumRe += chSpec[c][k].Real;
                        sumIm += chSpec[c][k].Imaginary;
                        refCount++;
                    }
                    if (refCount == 0) {
                        for (int c = 0; c < chCount; c++) {
                            if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                continue;
                            }
                            sumRe += chSpec[c][k].Real;
                            sumIm += chSpec[c][k].Imaginary;
                            refCount++;
                        }
                    }
                    float invR = refCount > 0 ? (1f / refCount) : 1f;
                    vRef = new Complex(sumRe * invR, sumIm * invR);
                    bandW = vocalBandWeight[k];
                }

                float vv = vRef.Real * vRef.Real + vRef.Imaginary * vRef.Imaginary;
                covVV[k] += a * (vv - covVV[k]);

                float leadPurity = 1f;
                if (preserveBackingVocals && hasNeuralBacking) {
                    float bb = refBackingSpec[k].Real * refBackingSpec[k].Real + refBackingSpec[k].Imaginary * refBackingSpec[k].Imaginary;
                    covBB[k] += a * (bb - covBB[k]);
                    leadPurity = covVV[k] / (covVV[k] + covBB[k] + Epsilon);
                }

                // Stereo Side protection when PreserveBackingVocals is enabled without an isolated neural lead stem
                float stereoCenterPurity = 1f;
                if (!useIsolatedNeuralLead && preserveBackingVocals && chCount >= 2) {
                    float dRe = chSpec[0][k].Real - chSpec[1][k].Real;
                    float dIm = chSpec[0][k].Imaginary - chSpec[1][k].Imaginary;
                    float sidePow = 0.25f * (dRe * dRe + dIm * dIm);
                    stereoCenterPurity = vv / (vv + sidePow + Epsilon);
                }

                vocalEnergySum += covVV[k] * bandW * leadPurity * stereoCenterPurity;

                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    Complex x = chSpec[c][k];
                    float cc = x.Real * x.Real + x.Imaginary * x.Imaginary;
                    totalEnergySum += cc;

                    float cvRe = x.Real * vRef.Real + x.Imaginary * vRef.Imaginary;
                    float cvIm = x.Imaginary * vRef.Real - x.Real * vRef.Imaginary;

                    covCC[c][k] += a * (cc - covCC[c][k]);
                    covCVRe[c][k] += a * (cvRe - covCVRe[c][k]);
                    covCVIm[c][k] += a * (cvIm - covCVIm[c][k]);

                    float denom = covCC[c][k] * covVV[k] + Epsilon;
                    float cohSq = (covCVRe[c][k] * covCVRe[c][k] + covCVIm[c][k] * covCVIm[c][k]) / denom;
                    if (cohSq > 1f) {
                        cohSq = 1f;
                    }

                    float vocalDominance = useIsolatedNeuralLead
                        ? MathF.Min(1f, covVV[k] / (covCC[c][k] + Epsilon))
                        : stereoCenterPurity;

                    float vocalMask = MathF.Sqrt(cohSq * vocalDominance) * bandW * leadPurity;
                    if (vocalMask > 1f) {
                        vocalMask = 1f;
                    }
                    float gain = 1f - attenStrength * vocalMask;
                    if (gain < 0f) {
                        gain = 0f;
                    }

                    Complex y = new Complex(x.Real * gain, x.Imaginary * gain);
                    chSpec[c][k] = y;
                    if (k > 0 && k < SpectrumBins - 1) {
                        chSpec[c][FftSize - k] = new Complex(y.Real, -y.Imaginary);
                    }
                }
            }

            if (totalEnergySum > Epsilon) {
                float rawAct = (float)(vocalEnergySum / (totalEnergySum / Math.Max(1, chCount) + Epsilon));
                if (rawAct < 0f) {
                    rawAct = 0f;
                } else if (rawAct > 1f) {
                    rawAct = 1f;
                }
                latestVocalActivity += 0.25f * (rawAct - latestVocalActivity);
            } else {
                latestVocalActivity *= 0.85f;
            }

            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                chSpec[c].InPlaceIFFT(fftCache);
                for (int n = 0; n < HopSize; n++) {
                    readyOut[c][n] = olaTail[c][n] + chSpec[c][n].Real * window[n];
                    olaTail[c][n] = chSpec[c][n + HopSize].Real * window[n + HopSize];
                }
            }
        }

        void FinishHop(int chCount) {
            for (int c = 0; c < chCount; c++) {
                Array.Copy(currIn[c], prevIn[c], HopSize);
            }
            Array.Copy(currRefLead, prevRefLead, HopSize);
            Array.Copy(currRefBacking, prevRefBacking, HopSize);
            inFill = 0;
        }

        static float ComputeVocalBandEnvelope(float freqHz) {
            // Smooth raised-cosine transitions: 0 below 100 Hz, 1.0 across 220..6500 Hz, gently tapering to 0.15 above 11 kHz
            if (freqHz <= 100f) {
                return 0f;
            }
            if (freqHz < 220f) {
                float t = (freqHz - 100f) / 120f;
                return 0.5f * (1f - MathF.Cos(MathF.PI * t));
            }
            if (freqHz <= 6500f) {
                return 1f;
            }
            if (freqHz < 11500f) {
                float t = (freqHz - 6500f) / 5000f;
                return 0.15f + 0.85f * 0.5f * (1f + MathF.Cos(MathF.PI * t));
            }
            return 0.15f;
        }

        static void FillFrame(float[] src, int start, int fftSize, float[] win, Complex[] dst) {
            int len = src.Length;
            for (int i = 0; i < fftSize; i++) {
                int idx = start + i;
                float s = (idx >= 0 && idx < len) ? src[idx] : 0f;
                dst[i] = new Complex(s * win[i], 0f);
            }
        }

        static int ClampChannels(int ch) {
            if (ch < 1) {
                return 1;
            }
            if (ch > MaxSpatialChannels) {
                return MaxSpatialChannels;
            }
            return ch;
        }

        /// <inheritdoc/>
        public void Dispose() {
            fftCache.Dispose();
        }
    }
}
