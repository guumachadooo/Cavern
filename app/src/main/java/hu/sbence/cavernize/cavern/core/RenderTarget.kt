package hu.sbence.cavernize.cavern.core

/**
 * Downmix matrix rule: mixes [sourceIndex] (or phase-inverted if [invertPhase] is true) into [targetIndex].
 */
data class DownmixPair(
    val sourceIndex: Int,
    val targetIndex: Int,
    val invertPhase: Boolean = false
)

enum class RenderTargetCategory(val labelPt: String, val labelEn: String) {
    STANDARD("Padrão (Home Theater)", "Standard (Home Theater)"),
    ATMOS_HEIGHT("Imersivo com Altura (3D)", "Immersive Height (3D)"),
    MATRIX_DOWNMIX("Matriz / Downmix", "Matrix / Downmix"),
    VIRTUAL_SPECIAL("Virtualizador & Driver", "Virtualizer & Driver")
}

open class RenderTarget(
    val name: String,
    val channels: Array<ReferenceChannel>,
    val category: RenderTargetCategory = RenderTargetCategory.STANDARD,
    val downmixPairs: List<DownmixPair> = emptyList(),
    val isVirtualizer: Boolean = false,
    val isDriverDefault: Boolean = false
) {
    open val outputChannels: Int
        get() = if (isVirtualizer) 2 else channels.size - downmixPairs.map { it.sourceIndex }.distinct().size

    fun isExported(channelIndex: Int): Boolean {
        if (isVirtualizer || isDriverDefault) return true
        return downmixPairs.none { it.sourceIndex == channelIndex }
    }

    /**
     * Prevent height limiting; require at least 4 overhead channels for full gain (from RenderTarget.cs).
     */
    fun getSafeGain(): Float {
        if (isVirtualizer) return 0.707f
        val topCount = channels.count { it.isHeightChannel }
        return if (topCount in 1..3) 0.70794576f else 1f
    }

    fun getEffectiveChannels(surroundSwap: Boolean): Array<ReferenceChannel> {
        if (!surroundSwap) return channels
        return Array(channels.size) { idx ->
            when (val ch = channels[idx]) {
                ReferenceChannel.SideLeft -> ReferenceChannel.RearLeft
                ReferenceChannel.SideRight -> ReferenceChannel.RearRight
                ReferenceChannel.RearLeft -> ReferenceChannel.SideLeft
                ReferenceChannel.RearRight -> ReferenceChannel.SideRight
                else -> ch
            }
        }
    }

    /**
     * Generates jack/wiring pairs (e.g., Jack 1: FL + FR, Jack 2: FC + LFE...) just like CavernizeGUI DisplayWiring.
     */
    fun getWiringMap(surroundSwap: Boolean, isPtBr: Boolean): List<Pair<String, String>> {
        if (isVirtualizer) {
            return listOf(
                (if (isPtBr) "Saída Fone Estéreo (P2 / USB)" else "Headphone Stereo Jack") to
                    (if (isPtBr) "Esquerda Binaural (L) + Direita Binaural (R) [HRTF 3D]" else "Binaural Left (L) + Binaural Right (R) [3D HRTF]")
            )
        }
        val activeList = mutableListOf<String>()
        val eff = getEffectiveChannels(surroundSwap)
        for (i in eff.indices) {
            if (isExported(i)) {
                val proto = ChannelPrototype.fromReference(eff[i])
                val matrixSources = downmixPairs.filter { it.targetIndex == i }.map { pair ->
                    val prefix = if (pair.invertPhase) "-Δ " else "+ "
                    prefix + eff[pair.sourceIndex].shortMark
                }
                val suffix = if (matrixSources.isNotEmpty()) " [Mix ${matrixSources.joinToString(" ")}]" else ""
                activeList.add("${eff[i].shortMark} (${proto.name})$suffix")
            }
        }
        val pairs = mutableListOf<Pair<String, String>>()
        var jackNum = 1
        var idx = 0
        while (idx < activeList.size) {
            val left = activeList[idx]
            val right = if (idx + 1 < activeList.size) activeList[idx + 1] else (if (isPtBr) "Desconectado (N/C)" else "Unconnected (N/C)")
            val label = if (isPtBr) "Conector Estéreo #$jackNum (Canais ${idx + 1}/${idx + 2})" else "Stereo Jack #$jackNum (Ch ${idx + 1}/${idx + 2})"
            pairs.add(label to "$left  •  $right")
            jackNum++
            idx += 2
        }
        return pairs
    }

    /**
     * Applies downmix matrix rules to a multi-channel frame buffer.
     */
    fun applyDownmixAndFilter(rawChannels: Array<FloatArray>): Array<FloatArray> {
        if (isVirtualizer) {
            return VirtualizerEngine.renderBinaural(rawChannels, channels)
        }
        if (downmixPairs.isEmpty()) return rawChannels

        val working = Array(rawChannels.size) { ch -> rawChannels[ch].clone() }
        for (pair in downmixPairs) {
            if (pair.sourceIndex in working.indices && pair.targetIndex in working.indices) {
                val src = working[pair.sourceIndex]
                val dst = working[pair.targetIndex]
                val sign = if (pair.invertPhase) -0.7071f else 0.7071f
                for (s in dst.indices) {
                    dst[s] = (dst[s] + src[s] * sign).coerceIn(-1f, 1f)
                }
            }
        }
        val exportedIndices = working.indices.filter { isExported(it) }
        return Array(exportedIndices.size) { idx -> working[exportedIndices[idx]] }
    }

    companion object {
        private val side512 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight, ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )

        private val side712 = arrayOf(
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
            ReferenceChannel.RearLeft, ReferenceChannel.RearRight, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        )

        private fun pair(src: Int, dst: Int): DownmixPair =
            if (src < 0) DownmixPair(src.inv(), dst, invertPhase = true)
            else DownmixPair(src, dst, invertPhase = false)

        /**
         * Complete 31 RenderTargets from Cavernize.Logic.Models.RenderTargets.RenderTarget.Targets.
         */
        val Targets: List<RenderTarget> = listOf(
            RenderTarget("2.0", ChannelPrototype.ref200, RenderTargetCategory.STANDARD),
            RenderTarget("4.0", ChannelPrototype.ref400, RenderTargetCategory.STANDARD),
            RenderTarget("3.1.2", ChannelPrototype.ref312, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("4.0.4", ChannelPrototype.ref404, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("4.1.1", ChannelPrototype.ref411, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("4.1.3", ChannelPrototype.ref413, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("5.1 side", ChannelPrototype.ref510, RenderTargetCategory.STANDARD),
            RenderTarget(
                "5.1 rear",
                arrayOf(
                    ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                    ReferenceChannel.RearLeft, ReferenceChannel.RearRight
                ),
                RenderTargetCategory.STANDARD
            ),
            RenderTarget("5.1.2 side", side512, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget(
                "5.1.2 front",
                ChannelPrototype.ref514,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(8, 4), pair(9, 5))
            ),
            RenderTarget(
                "5.1.2 matrix",
                side512,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(6, 0), pair(6.inv(), 4), pair(7, 1), pair(7.inv(), 5))
            ),
            RenderTarget("5.1.4", ChannelPrototype.ref514, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget(
                "5.1.4 matrix",
                ChannelPrototype.ref514,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(8, 0), pair(8.inv(), 4), pair(9, 1), pair(9.inv(), 5))
            ),
            RenderTarget("5.1.6 with top sides", ChannelPrototype.ref516, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("5.1.6 for WAVE", ChannelPrototype.wav516, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("7.1", ChannelPrototype.ref710, RenderTargetCategory.STANDARD),
            RenderTarget("7.1.2 side", side712, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget(
                "7.1.2 front",
                ChannelPrototype.ref714,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(10, 4), pair(11, 5))
            ),
            RenderTarget(
                "7.1.2 matrix",
                side712,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(8, 6), pair(8.inv(), 4), pair(9, 7), pair(9.inv(), 5))
            ),
            RenderTarget(
                "7.1.3 matrix",
                arrayOf(
                    ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                    ReferenceChannel.RearLeft, ReferenceChannel.RearRight, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                    ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight, ReferenceChannel.TopFrontCenter
                ),
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(8, 6), pair(8.inv(), 4), pair(9, 7), pair(9.inv(), 5), pair(10, 0), pair(10.inv(), 1))
            ),
            RenderTarget("7.1.4", ChannelPrototype.ref714, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("7.1.6 with top sides", ChannelPrototype.ref716, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("7.1.6 for WAVE", ChannelPrototype.wav716, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("9.1", ChannelPrototype.ref910, RenderTargetCategory.STANDARD),
            RenderTarget(
                "9.1.2 side",
                arrayOf(
                    ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                    ReferenceChannel.RearLeft, ReferenceChannel.RearRight, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                    ReferenceChannel.WideLeft, ReferenceChannel.WideRight, ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                ),
                RenderTargetCategory.ATMOS_HEIGHT
            ),
            RenderTarget(
                "9.1.2 front",
                ChannelPrototype.ref914,
                RenderTargetCategory.MATRIX_DOWNMIX,
                listOf(pair(12, 4), pair(13, 5))
            ),
            RenderTarget("9.1.4", ChannelPrototype.ref914, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("9.1.6 with top sides", ChannelPrototype.ref916, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget("9.1.6 for WAVE", ChannelPrototype.wav916, RenderTargetCategory.ATMOS_HEIGHT),
            RenderTarget(
                "Driver default",
                ChannelPrototype.ref714,
                RenderTargetCategory.VIRTUAL_SPECIAL,
                isDriverDefault = true
            ),
            RenderTarget(
                "Headphone Virtualizer",
                ChannelPrototype.ref714,
                RenderTargetCategory.VIRTUAL_SPECIAL,
                isVirtualizer = true
            )
        )
    }
}
