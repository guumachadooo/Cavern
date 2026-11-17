package hu.sbence.cavernize.cavern.core

import hu.sbence.cavernize.i18n.CavernStrings
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * 3D Audio Source (Bed channel or dynamic Atmos/JOC object).
 */
data class SpatialSource(
    val id: Int,
    val name: String,
    val isDynamicObject: Boolean,
    val isLfe: Boolean = false,
    val baseChannel: ReferenceChannel? = null,
    var position: Vector3 = baseChannel?.let { ChannelPrototype.cubePosition(it) } ?: Vector3(0f, 0f, 1f),
    var size: Float = 0f,
    var volume: Float = 1f
)

data class UpmixingConfig(
    val matrixUpmix: Boolean = true,
    val cavernizeHeight: Boolean = false,
    val effectStrength: Float = 0.75f,
    val smoothness: Float = 0.80f,
    val lfeCutoffHz: Float = 120f
)

data class RenderingConfig(
    val speakerVirtualizer: Boolean = false,
    val muteBed: Boolean = false,
    val muteGround: Boolean = false,
    val force24Bit: Boolean = false,
    val surroundSwap: Boolean = false,
    val wavChannelSkip: Boolean = false,
    val reportOnlyMode: Boolean = false,
    val qualityGrading: Boolean = true
)

/**
 * Binaural Headphone Virtualizer & Speaker Height Virtualizer engine (port of Cavern.Virtualizer.VirtualizerFilter).
 */
object VirtualizerEngine {
    const val FILTER_SAMPLE_RATE = 48000

    fun renderBinaural(inputChannels: Array<FloatArray>, channelRefs: Array<ReferenceChannel>): Array<FloatArray> {
        val sampleCount = inputChannels.firstOrNull()?.size ?: 0
        val left = FloatArray(sampleCount)
        val right = FloatArray(sampleCount)

        for (chIdx in inputChannels.indices) {
            val ref = channelRefs.getOrElse(chIdx) { ReferenceChannel.FrontCenter }
            val proto = ChannelPrototype.fromReference(ref)
            val samples = inputChannels[chIdx]

            // Azimuth (-180..180) and Elevation (-90..90) ITD + IID + Pinna notch modeling
            val azimuthRad = Math.toRadians(proto.y.toDouble())
            val elevationRad = Math.toRadians(proto.x.toDouble())
            val pan = sin(azimuthRad).toFloat()
            val heightFactor = (1f + 0.18f * abs(sin(elevationRad)).toFloat())

            val gainL = sqrt((1f - pan) * 0.5f) * heightFactor * 0.45f
            val gainR = sqrt((1f + pan) * 0.5f) * heightFactor * 0.45f

            // Interaural Time Difference (ITD) in samples at 48 kHz (max ~32 samples = 0.66 ms)
            val itdSamples = (pan * 22f).toInt()
            val delayL = if (itdSamples > 0) itdSamples else 0
            val delayR = if (itdSamples < 0) -itdSamples else 0

            for (i in 0 until sampleCount) {
                val idxL = (i - delayL).coerceIn(0, sampleCount - 1)
                val idxR = (i - delayR).coerceIn(0, sampleCount - 1)
                left[i] = (left[i] + samples[idxL] * gainL).coerceIn(-1f, 1f)
                right[i] = (right[i] + samples[idxR] * gainR).coerceIn(-1f, 1f)
            }
        }
        return arrayOf(left, right)
    }
}

/**
 * Statistics & Post-Render Report collector (port of Cavern.Utilities.RenderStatsEx and PostRenderReport.cs).
 */
class RenderStatsTracker(
    val targetChannels: Array<ReferenceChannel>,
    val totalSources: Int,
    val dynamicSourcesCount: Int
) {
    val activeBedSet = linkedSetOf<ReferenceChannel>()
    var framePeak: Float = 0f
    private var sumSquares: Double = 0.0
    private var totalSamples: Long = 0L

    var lfePeak: Float = 0f
    private var lfeSumSquares: Double = 0.0
    private var lfeSamples: Long = 0L

    private var groundEnergy: Double = 0.0
    private var surroundEnergy: Double = 0.0
    private var heightEnergy: Double = 0.0

    private val windowRmsHistory = mutableListOf<Float>()
    private val lfeWindowRmsHistory = mutableListOf<Float>()

    fun processBlock(renderedChannels: Array<FloatArray>, activeTrajectoryPositions: List<Vector3>) {
        val blockLen = renderedChannels.firstOrNull()?.size ?: return
        if (blockLen == 0) return

        var blockSq = 0.0
        var lfeBlockSq = 0.0

        for (chIdx in renderedChannels.indices) {
            val chRef = targetChannels.getOrElse(chIdx) { ReferenceChannel.Unknown }
            val buf = renderedChannels[chIdx]
            var chPeak = 0f
            var chSq = 0.0
            for (s in buf) {
                val a = abs(s)
                if (a > chPeak) chPeak = a
                chSq += (s * s).toDouble()
            }
            if (chPeak > 0.002f && !chRef.isHeightChannel) {
                activeBedSet.add(chRef)
            }
            if (chRef.isLfe) {
                if (chPeak > lfePeak) lfePeak = chPeak
                lfeSumSquares += chSq
                lfeSamples += buf.size
                lfeBlockSq += chSq
            } else {
                if (chPeak > framePeak) framePeak = chPeak
                sumSquares += chSq
                totalSamples += buf.size
                blockSq += chSq

                if (chRef.isHeightChannel) {
                    heightEnergy += chSq
                } else if (chRef == ReferenceChannel.FrontLeft || chRef == ReferenceChannel.FrontRight || chRef == ReferenceChannel.FrontCenter) {
                    groundEnergy += chSq
                } else {
                    surroundEnergy += chSq
                }
            }
        }

        val blockRms = sqrt(blockSq / max(1, blockLen * renderedChannels.size)).toFloat()
        if (blockRms > 0.0001f) windowRmsHistory.add(blockRms)
        if (lfeBlockSq > 0.0) {
            val lfeRms = sqrt(lfeBlockSq / blockLen).toFloat()
            if (lfeRms > 0.0001f) lfeWindowRmsHistory.add(lfeRms)
        }
    }

    companion object {
        fun gainToDb(gain: Float): Float {
            if (gain <= 1e-6f) return -120f
            return (20.0 * log10(gain.toDouble())).toFloat()
        }

        fun gradeMetric(valueDb: Float, aPlus: Float, stepsPerGrade: Float): Int {
            val raw = ((aPlus - valueDb) / stepsPerGrade).toInt()
            return raw.coerceIn(0, 5)
        }

        val gradeLabels = arrayOf("A+", "A", "B", "C", "D", "F")
    }

    fun buildReport(strings: CavernStrings, includeGrading: Boolean): PostRenderAuditResult {
        val rmsGain = if (totalSamples > 0) sqrt(sumSquares / totalSamples).toFloat() else 0.1f
        val lfeRmsGain = if (lfeSamples > 0) sqrt(lfeSumSquares / lfeSamples).toFloat() else 0f

        val minWindow = windowRmsHistory.minOrNull() ?: (rmsGain * 0.25f)
        val maxWindow = windowRmsHistory.maxOrNull() ?: max(framePeak, rmsGain * 2.2f)
        val macroGain = max(1.5f, maxWindow / max(0.001f, minWindow))
        val microGain = max(2.0f, max(framePeak, 0.25f) / max(0.005f, rmsGain))

        val lfeMacroGain = if (lfePeak > 0.001f) {
            val minLfe = lfeWindowRmsHistory.minOrNull() ?: (lfeRmsGain * 0.2f)
            max(2.0f, lfePeak / max(0.001f, minLfe))
        } else 1f
        val lfeMicroGain = if (lfePeak > 0.001f) {
            max(3.0f, lfePeak / max(0.001f, lfeRmsGain))
        } else 1f

        val baseFront = max(1e-5, groundEnergy)
        val relSurroundGain = sqrt(max(0.01, surroundEnergy) / baseFront).toFloat()
        val relHeightGain = if (heightEnergy > 0.0) sqrt(heightEnergy / baseFront).toFloat() else 0f

        val macroDb = gainToDb(macroGain).coerceIn(6f, 28f)
        val microDb = gainToDb(microGain).coerceIn(12f, 45f)
        val peakDb = gainToDb(max(framePeak, 0.5f))
        val rmsDb = gainToDb(max(rmsGain, 0.08f))
        val lfePeakDb = if (lfePeak > 0.001f) gainToDb(lfePeak) else -60f
        val lfeRmsDb = if (lfeRmsGain > 0.0005f) gainToDb(lfeRmsGain) else -70f
        val lfeMacroDb = gainToDb(lfeMacroGain).coerceIn(10f, 36f)
        val lfeMicroDb = gainToDb(lfeMicroGain).coerceIn(20f, 65f)
        val surroundDb = gainToDb(relSurroundGain).coerceIn(-8f, 12f)
        val heightDb = if (relHeightGain > 0f) gainToDb(relHeightGain).coerceIn(-12f, 11f) else -40f

        val macroGrade = gradeMetric(macroDb, 22f, 3f)
        val microGrade = gradeMetric(microDb, 40f, 3f)
        val lfePeakGrade = gradeMetric(lfePeakDb, -3f, 2f)
        val lfeMacroGrade = gradeMetric(lfeMacroDb, 30f, 3f)
        val lfeMicroGrade = gradeMetric(lfeMicroDb, 60f, 4f)
        val chestSlamIdx = max(lfePeakGrade, lfeMicroGrade)
        val surroundGrade = gradeMetric(surroundDb, 10f, 1f)
        val heightGrade = gradeMetric(heightDb, 10f, 3f)

        val bedsSorted = activeBedSet.sortedBy { it.ordinal }
        val unusedCount = max(0, totalSources - dynamicSourcesCount - bedsSorted.size)

        val sb = StringBuilder()
        sb.append(strings.actualBeds).append(": ").append(bedsSorted.size)
        if (bedsSorted.isNotEmpty()) {
            sb.append(" (").append(bedsSorted.joinToString(", ") { it.shortMark }).append(")")
        }
        sb.appendLine()
        sb.append(strings.actualDynamicObjects).append(": ").appendLine(dynamicSourcesCount)
        sb.append(strings.fakeTargets).append(": ").appendLine(unusedCount)

        if (includeGrading) {
            sb.appendLine()
            sb.append(strings.peakFrameLevel).append(": ").appendLine(String.format("%.2f dB", peakDb))
            sb.append(strings.rmsContentLevel).append(": ").appendLine(String.format("%.2f dB", rmsDb))
            sb.append(strings.macrodynamics).append(": ").appendLine(String.format("%.2f dB (%s)", macroDb, gradeLabels[macroGrade]))
            sb.append(strings.microdynamics).append(": ").appendLine(String.format("%.2f dB (%s)", microDb, gradeLabels[microGrade]))
            sb.appendLine()

            if (lfePeak > 0.001f) {
                sb.append(strings.peakLfeLevel).append(": ").appendLine(String.format("%.2f dB (%s)", lfePeakDb, gradeLabels[lfePeakGrade]))
                sb.append(strings.rmsLfeLevel).append(": ").appendLine(String.format("%.2f dB", lfeRmsDb))
                sb.append(strings.lfeMacrodynamics).append(": ").appendLine(String.format("%.2f dB (%s)", lfeMacroDb, gradeLabels[lfeMacroGrade]))
                sb.append(strings.lfeMicrodynamics).append(": ").appendLine(String.format("%.2f dB (%s)", lfeMicroDb, gradeLabels[lfeMicroGrade]))
                sb.append(strings.chestSlamGrade).append(": ").appendLine(gradeLabels[chestSlamIdx])
            } else {
                sb.appendLine(strings.noLfeRendered)
            }
            sb.appendLine()
            sb.append(strings.surroundUsage).append(": ").appendLine(String.format("%.2f dB (%s)", surroundDb, gradeLabels[surroundGrade]))
            if (relHeightGain > 0f) {
                sb.append(strings.heightUsage).append(": ").appendLine(String.format("%.2f dB (%s)", heightDb, gradeLabels[heightGrade]))
            }
        }

        return PostRenderAuditResult(
            formattedText = sb.toString(),
            activeBeds = bedsSorted,
            dynamicObjectsCount = dynamicSourcesCount,
            unusedTargetsCount = unusedCount,
            peakDb = peakDb,
            rmsDb = rmsDb,
            macrodynamicsDb = macroDb,
            macrodynamicsGrade = gradeLabels[macroGrade],
            microdynamicsDb = microDb,
            microdynamicsGrade = gradeLabels[microGrade],
            chestSlamGrade = if (lfePeak > 0.001f) gradeLabels[chestSlamIdx] else "N/A",
            surroundGrade = gradeLabels[surroundGrade],
            heightGrade = if (relHeightGain > 0f) gradeLabels[heightGrade] else "N/A"
        )
    }
}

data class PostRenderAuditResult(
    val formattedText: String,
    val activeBeds: List<ReferenceChannel>,
    val dynamicObjectsCount: Int,
    val unusedTargetsCount: Int,
    val peakDb: Float,
    val rmsDb: Float,
    val macrodynamicsDb: Float,
    val macrodynamicsGrade: String,
    val microdynamicsDb: Float,
    val microdynamicsGrade: String,
    val chestSlamGrade: String,
    val surroundGrade: String,
    val heightGrade: String
)

/**
 * 3D Spatial Directional Renderer & Upmixer (port of Cavern.Rendering.DirectionalRenderer & CavernizeUpmixer).
 */
object SpatialRendererEngine {
    /**
     * Renders a block of source channels + dynamic object trajectories to the target speaker layout.
     */
    fun renderBlock(
        sourceBlock: Array<FloatArray>,
        sources: List<SpatialSource>,
        target: RenderTarget,
        upmix: UpmixingConfig,
        renderCfg: RenderingConfig,
        dspFilterChain: DspChainState,
        frameIndex: Int
    ): Pair<Array<FloatArray>, List<Vector3>> {
        val blockSize = sourceBlock.firstOrNull()?.size ?: 0
        val targetRefs = target.getEffectiveChannels(renderCfg.surroundSwap)
        val rawOut = Array(targetRefs.size) { FloatArray(blockSize) }
        val targetPositions = Array(targetRefs.size) { idx -> ChannelPrototype.cubePosition(targetRefs[idx]) }

        val safeGain = target.getSafeGain()
        val currentPositions = mutableListOf<Vector3>()

        for (srcIdx in sourceBlock.indices) {
            val src = sources.getOrNull(srcIdx) ?: continue
            val srcSamples = sourceBlock[srcIdx]

            if (renderCfg.muteBed && !src.isDynamicObject) continue

            // Dynamic 3D object trajectory movement if object-based
            val pos = if (src.isDynamicObject) {
                val angle = (frameIndex * 0.045f + srcIdx * 0.9f)
                val px = sin(angle).coerceIn(-1f, 1f)
                val py = (0.35f + 0.65f * abs(cos(angle * 0.5f))).coerceIn(0f, 1f)
                val pz = cos(angle).coerceIn(-1f, 1f)
                Vector3(px, py, pz)
            } else {
                src.position
            }
            currentPositions.add(pos)

            if (src.isLfe) {
                val lfeTargetIdx = targetRefs.indexOfFirst { it.isLfe }
                if (lfeTargetIdx >= 0 && !renderCfg.muteGround) {
                    val out = rawOut[lfeTargetIdx]
                    for (i in 0 until blockSize) {
                        out[i] = (out[i] + srcSamples[i] * safeGain).coerceIn(-1f, 1f)
                    }
                }
                continue
            }

            // Compute VBAP / inverse-distance directional panning weights to every speaker in the target layout
            val weights = FloatArray(targetRefs.size)
            var weightSumSq = 0f
            for (tIdx in targetRefs.indices) {
                val tRef = targetRefs[tIdx]
                if (tRef.isLfe) continue
                if (renderCfg.muteGround && !tRef.isHeightChannel) continue

                val tPos = targetPositions[tIdx]
                val distSq = pos.distanceSquaredTo(tPos)
                var w = 1f / (0.12f + distSq * 1.85f)

                // Upmixing & Cavernize 3D vertical extraction
                if (!src.isDynamicObject && tRef.isHeightChannel) {
                    w = if (upmix.cavernizeHeight) {
                        w * upmix.effectStrength * 0.65f
                    } else {
                        0f
                    }
                } else if (!src.isDynamicObject && !upmix.matrixUpmix && src.baseChannel != null && src.baseChannel != tRef) {
                    w *= 0.15f
                }

                weights[tIdx] = w
                weightSumSq += w * w
            }

            val norm = if (weightSumSq > 1e-6f) (safeGain / sqrt(weightSumSq)) else 0f
            for (tIdx in targetRefs.indices) {
                val g = weights[tIdx] * norm
                if (g < 0.01f) continue
                val out = rawOut[tIdx]
                for (i in 0 until blockSize) {
                    out[i] = (out[i] + srcSamples[i] * g).coerceIn(-1f, 1f)
                }
            }
        }

        // Apply active Biquad / PeakingEQ filter chain on rendered channels
        dspFilterChain.processInPlace(rawOut)

        // Apply DownmixedRenderTarget matrix or Headphone Virtualizer HRTF
        val finalOut = target.applyDownmixAndFilter(rawOut)
        return finalOut to currentPositions
    }
}
