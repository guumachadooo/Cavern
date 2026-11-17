package hu.sbence.cavernize.cavern.core

import kotlin.math.PI
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * All Biquad filter types from Cavern.Filters.BiquadFilterType.
 */
enum class BiquadFilterType(val displayNamePt: String, val displayNameEn: String, val eqApoCode: String) {
    PeakingEQ("Peaking EQ (Bell)", "Peaking EQ (Bell)", "PK"),
    Lowpass("Passa-Baixas (Lowpass)", "Lowpass (LP)", "LP"),
    Highpass("Passa-Altas (Highpass)", "Highpass (HP)", "HP"),
    Bandpass("Passa-Faixa (Bandpass)", "Bandpass (BP)", "BP"),
    Notch("Rejeita-Faixa (Notch)", "Notch (NO)", "NO"),
    Allpass("Passa-Tudo (Allpass)", "Allpass (AP)", "AP"),
    LowShelf("Low Shelf (Grave)", "Low Shelf (LS)", "LSC"),
    HighShelf("High Shelf (Agudo)", "High Shelf (HS)", "HSC")
}

data class BiquadCoefficients(
    val b0: Double,
    val b1: Double,
    val b2: Double,
    val a1: Double,
    val a2: Double
) {
    /**
     * Evaluates complex transfer function H(z) at frequency [freqHz] and sampleRate [sampleRate].
     * Returns Pair(magnitudeDb, phaseDegrees).
     */
    fun evaluateAt(freqHz: Double, sampleRate: Int = 48000): Pair<Float, Float> {
        val omega = 2.0 * PI * freqHz / sampleRate
        val cosW = cos(omega)
        val sinW = sin(omega)
        val cos2W = cos(2.0 * omega)
        val sin2W = sin(2.0 * omega)

        // Numerator B(e^-jw) = b0 + b1*e^-jw + b2*e^-j2w
        val numRe = b0 + b1 * cosW + b2 * cos2W
        val numIm = -b1 * sinW - b2 * sin2W

        // Denominator A(e^-jw) = 1 + a1*e^-jw + a2*e^-j2w
        val denRe = 1.0 + a1 * cosW + a2 * cos2W
        val denIm = -a1 * sinW - a2 * sin2W

        val denMagSq = (denRe * denRe + denIm * denIm).coerceAtLeast(1e-18)
        val hRe = (numRe * denRe + numIm * denIm) / denMagSq
        val hIm = (numIm * denRe - numRe * denIm) / denMagSq

        val mag = sqrt(hRe * hRe + hIm * hIm).coerceAtLeast(1e-6)
        val magDb = (20.0 * log10(mag)).toFloat().coerceIn(-40f, 40f)
        val phaseDeg = Math.toDegrees(atan2(hIm, hRe)).toFloat()
        return magDb to phaseDeg
    }
}

/**
 * Port of Cavern.Filters.BiquadFilter + PhaseSwappableBiquadFilter.
 */
data class BiquadFilterConfig(
    val id: Int = 0,
    val type: BiquadFilterType = BiquadFilterType.PeakingEQ,
    val centerFreqHz: Double = 632.456,
    val q: Double = 0.7071,
    val gainDb: Double = 0.0,
    val swapPhase: Boolean = false,
    val enabled: Boolean = true
) {
    fun computeCoefficients(sampleRate: Int = 48000): BiquadCoefficients {
        val freq = centerFreqHz.coerceIn(10.0, sampleRate * 0.48)
        val safeQ = q.coerceIn(0.1, 24.0)
        val w0 = 2.0 * PI * freq / sampleRate
        val cosW0 = cos(w0)
        val sinW0 = sin(w0)
        val alpha = sinW0 / (2.0 * safeQ)
        val a = 10.0.pow(gainDb / 40.0)

        var b0: Double
        var b1: Double
        var b2: Double
        var a0: Double
        var a1: Double
        var a2: Double

        when (type) {
            BiquadFilterType.Lowpass -> {
                b1 = 1.0 - cosW0
                b0 = b1 * 0.5
                b2 = b0
                a0 = 1.0 + alpha
                a1 = -2.0 * cosW0
                a2 = 1.0 - alpha
            }
            BiquadFilterType.Highpass -> {
                b0 = (1.0 + cosW0) * 0.5
                b1 = -(1.0 + cosW0)
                b2 = b0
                a0 = 1.0 + alpha
                a1 = -2.0 * cosW0
                a2 = 1.0 - alpha
            }
            BiquadFilterType.Bandpass -> {
                b0 = sinW0 * 0.5
                b1 = 0.0
                b2 = -b0
                a0 = 1.0 + alpha
                a1 = -2.0 * cosW0
                a2 = 1.0 - alpha
            }
            BiquadFilterType.Notch -> {
                b0 = 1.0
                b1 = -2.0 * cosW0
                b2 = 1.0
                a0 = 1.0 + alpha
                a1 = b1
                a2 = 1.0 - alpha
            }
            BiquadFilterType.Allpass -> {
                b0 = 1.0 - alpha
                b1 = -2.0 * cosW0
                b2 = 1.0 + alpha
                a0 = b2
                a1 = b1
                a2 = b0
            }
            BiquadFilterType.PeakingEQ -> {
                b0 = 1.0 + alpha * a
                b1 = -2.0 * cosW0
                b2 = 1.0 - alpha * a
                a0 = 1.0 + alpha / a
                a1 = b1
                a2 = 1.0 - alpha / a
            }
            BiquadFilterType.LowShelf -> {
                val twoSqrtAAlpha = 2.0 * sqrt(a) * alpha
                b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha)
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0)
                b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha)
                a0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0)
                a2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha
            }
            BiquadFilterType.HighShelf -> {
                val twoSqrtAAlpha = 2.0 * sqrt(a) * alpha
                b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha)
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0)
                b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha)
                a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0)
                a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha
            }
        }

        val sign = if (swapPhase) -1.0 else 1.0
        return BiquadCoefficients(
            b0 = sign * (b0 / a0),
            b1 = sign * (b1 / a0),
            b2 = sign * (b2 / a0),
            a1 = a1 / a0,
            a2 = a2 / a0
        )
    }

    fun toEqualizerApoLine(index: Int): String =
        String.format("Filter %d: ON %s Fc %.2f Hz Gain %.2f dB Q %.3f", index, type.eqApoCode, centerFreqHz, gainDb, q)
}

/**
 * Convolution / HRIR Impulse Response model (port of Cavern.WPF.ConvolutionEditor).
 */
data class ConvolutionImpulseState(
    val name: String,
    val sampleRate: Int,
    val samples: FloatArray,
    val isPositivePolarity: Boolean,
    val averagePhaseDegrees: Float,
    val delayMs: Float,
    val fftSpectrumDb: List<Pair<Float, Float>>
) {
    companion object {
        fun generateSyntheticHrir(name: String = "Cavern Built-in 3D HRIR (48 kHz)"): ConvolutionImpulseState {
            val sr = 48000
            val len = 256
            val ir = FloatArray(len)
            val delaySamples = 14
            for (i in delaySamples until len) {
                val t = (i - delaySamples).toFloat()
                val env = exp(-t / 28.0).toFloat()
                val earResonance = (sin(2.0 * PI * 3200.0 * t / sr) * 0.35 + cos(2.0 * PI * 950.0 * t / sr) * 0.65).toFloat()
                ir[i] = if (i == delaySamples) 0.92f else env * earResonance * 0.42f
            }
            return fromImpulseSamples(name, sr, ir)
        }

        fun fromImpulseSamples(name: String, sampleRate: Int, samples: FloatArray): ConvolutionImpulseState {
            var peakIdx = 0
            var maxAbs = 0f
            for (i in samples.indices) {
                val a = kotlin.math.abs(samples[i])
                if (a > maxAbs) {
                    maxAbs = a
                    peakIdx = i
                }
            }
            val positive = (samples.getOrElse(peakIdx) { 1f }) >= 0f
            val delayMs = (peakIdx * 1000f) / sampleRate.coerceAtLeast(1)

            // Compute 64 log-spaced frequency points (20 Hz .. 20 kHz) DFT magnitude & average phase
            val points = mutableListOf<Pair<Float, Float>>()
            var phaseSum = 0.0
            val numBins = 64
            val minLog = ln(20.0)
            val maxLog = ln(20000.0)
            val n = samples.size.coerceAtMost(512)

            for (b in 0 until numBins) {
                val freq = exp(minLog + (maxLog - minLog) * b / (numBins - 1))
                val omega = 2.0 * PI * freq / sampleRate
                var re = 0.0
                var im = 0.0
                for (k in 0 until n) {
                    val s = samples[k].toDouble()
                    re += s * cos(omega * k)
                    im -= s * sin(omega * k)
                }
                val mag = sqrt(re * re + im * im).coerceAtLeast(1e-5)
                val db = (20.0 * log10(mag)).toFloat().coerceIn(-36f, 24f)
                phaseSum += Math.toDegrees(atan2(im, re))
                points.add(freq.toFloat() to db)
            }
            val avgPhase = (phaseSum / numBins).toFloat()

            return ConvolutionImpulseState(
                name = name,
                sampleRate = sampleRate,
                samples = samples,
                isPositivePolarity = positive,
                averagePhaseDegrees = avgPhase,
                delayMs = delayMs,
                fftSpectrumDb = points
            )
        }
    }
}

/**
 * Runtime DSP Chain combining single Biquad editor, Multi-Band Parametric EQ, and Convolution HRIR.
 */
data class DspChainState(
    val activeBiquad: BiquadFilterConfig = BiquadFilterConfig(
        id = 1,
        type = BiquadFilterType.PeakingEQ,
        centerFreqHz = 632.456,
        q = 0.707,
        gainDb = 3.5,
        swapPhase = false,
        enabled = true
    ),
    val eqBands: List<BiquadFilterConfig> = listOf(
        BiquadFilterConfig(1, BiquadFilterType.LowShelf, 65.0, 0.707, 3.0),
        BiquadFilterConfig(2, BiquadFilterType.PeakingEQ, 250.0, 1.414, -2.0),
        BiquadFilterConfig(3, BiquadFilterType.PeakingEQ, 1200.0, 1.0, 1.5),
        BiquadFilterConfig(4, BiquadFilterType.PeakingEQ, 4200.0, 1.2, 2.5),
        BiquadFilterConfig(5, BiquadFilterType.HighShelf, 11000.0, 0.707, 2.0)
    ),
    val convolutionImpulse: ConvolutionImpulseState = ConvolutionImpulseState.generateSyntheticHrir(),
    val applyEqOnRender: Boolean = false,
    val applyConvolutionOnRender: Boolean = false
) {
    /**
     * Evaluates composite frequency response (20 Hz .. 20,000 Hz) across 80 log-spaced points.
     */
    fun evaluateCompositeCurve(includeSingleBiquad: Boolean, sampleRate: Int = 48000): List<Triple<Float, Float, Float>> {
        val minLog = ln(20.0)
        val maxLog = ln(20000.0)
        val points = 80
        val activeCoeffs = if (includeSingleBiquad) {
            listOf(activeBiquad.computeCoefficients(sampleRate))
        } else {
            eqBands.filter { it.enabled }.map { it.computeCoefficients(sampleRate) }
        }

        return List(points) { i ->
            val freq = exp(minLog + (maxLog - minLog) * i / (points - 1))
            var totalDb = 0f
            var totalPhase = 0f
            for (c in activeCoeffs) {
                val (db, ph) = c.evaluateAt(freq, sampleRate)
                totalDb += db
                totalPhase += ph
            }
            val wrappedPhase = (((totalPhase + 180f) % 360f + 360f) % 360f) - 180f
            Triple(freq.toFloat(), totalDb.coerceIn(-30f, 30f), wrappedPhase)
        }
    }

    fun exportEqualizerApoConfig(): String {
        val sb = StringBuilder()
        sb.appendLine("# Cavern QuickEQ / Equalizer APO Export")
        sb.appendLine("Preamp: -2.5 dB")
        eqBands.forEachIndexed { idx, band ->
            sb.appendLine(band.toEqualizerApoLine(idx + 1))
        }
        return sb.toString()
    }

    fun processInPlace(channels: Array<FloatArray>, sampleRate: Int = 48000) {
        if (!applyEqOnRender && !applyConvolutionOnRender) return
        val coeffs = eqBands.filter { it.enabled }.map { it.computeCoefficients(sampleRate) }
        for (ch in channels) {
            if (applyEqOnRender) {
                for (c in coeffs) {
                    var x1 = 0.0
                    var x2 = 0.0
                    var y1 = 0.0
                    var y2 = 0.0
                    for (i in ch.indices) {
                        val x0 = ch[i].toDouble()
                        val y0 = c.b0 * x0 + c.b1 * x1 + c.b2 * x2 - c.a1 * y1 - c.a2 * y2
                        x2 = x1
                        x1 = x0
                        y2 = y1
                        y1 = y0
                        ch[i] = y0.toFloat().coerceIn(-1f, 1f)
                    }
                }
            }
            if (applyConvolutionOnRender && convolutionImpulse.samples.isNotEmpty()) {
                val ir = convolutionImpulse.samples
                val taps = ir.size.coerceAtMost(32)
                val copy = ch.clone()
                for (i in ch.indices) {
                    var acc = 0f
                    var k = 0
                    while (k < taps && i - k >= 0) {
                        acc += copy[i - k] * ir[k]
                        k++
                    }
                    ch[i] = (acc * 0.65f).coerceIn(-1f, 1f)
                }
            }
        }
    }
}
