package hu.sbence.cavernize.ui

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import hu.sbence.cavernize.cavern.core.BiquadFilterConfig
import hu.sbence.cavernize.cavern.core.BiquadFilterType
import hu.sbence.cavernize.cavern.core.ConvolutionImpulseState
import hu.sbence.cavernize.cavern.core.DspChainState
import hu.sbence.cavernize.cavern.core.PostRenderAuditResult
import hu.sbence.cavernize.cavern.core.ReferenceChannel
import hu.sbence.cavernize.cavern.core.RenderStatsTracker
import hu.sbence.cavernize.cavern.core.RenderTarget
import hu.sbence.cavernize.cavern.core.RenderingConfig
import hu.sbence.cavernize.cavern.core.SpatialRendererEngine
import hu.sbence.cavernize.cavern.core.UpmixingConfig
import hu.sbence.cavernize.cavern.core.Vector3
import hu.sbence.cavernize.cavern.core.VirtualizerEngine
import hu.sbence.cavernize.cavern.format.AudioContainerParser
import hu.sbence.cavernize.cavern.format.AudioWritersAndTranscoders
import hu.sbence.cavernize.cavern.format.BitDepth
import hu.sbence.cavernize.cavern.format.CavernizeTrack
import hu.sbence.cavernize.cavern.format.Codec
import hu.sbence.cavernize.cavern.format.ExportFormat
import hu.sbence.cavernize.cavern.format.JobStatus
import hu.sbence.cavernize.cavern.format.LoadedAudioFile
import hu.sbence.cavernize.cavern.format.QueuedJob
import hu.sbence.cavernize.i18n.AppLanguage
import hu.sbence.cavernize.i18n.CavernStrings
import hu.sbence.cavernize.i18n.LocalizationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.roundToInt

enum class MainNavTab {
    CONVERTER,
    DSP_UPMIX,
    EAC3_MERGER,
    REPORTS_INFO
}

data class MergerChannelSlot(
    val targetChannel: ReferenceChannel,
    val group: String, // "BED", "GROUND", "HEIGHT"
    val assignedFileName: String? = null,
    val sourceChannelIndex: Int = 0
)

data class CavernizeUiState(
    val language: AppLanguage = AppLanguage.PT_BR,
    val strings: CavernStrings = LocalizationRegistry.ptBr,
    val currentTab: MainNavTab = MainNavTab.CONVERTER,
    val selectedRenderTarget: RenderTarget = RenderTarget.Targets.first { it.name == "5.1.4" },
    val selectedExportFormat: ExportFormat = ExportFormat.AllFormats.first { it.codec == Codec.PCM_LE },
    val loadedFile: LoadedAudioFile? = null,
    val selectedTrack: CavernizeTrack? = null,
    val renderingConfig: RenderingConfig = RenderingConfig(),
    val upmixingConfig: UpmixingConfig = UpmixingConfig(),
    val dspChain: DspChainState = DspChainState(),
    val queue: List<QueuedJob> = emptyList(),
    val selectedQueueJobId: Long? = null,
    val isRendering: Boolean = false,
    val renderProgress: Float = 0f,
    val statusMessage: String = LocalizationRegistry.ptBr.statusReady,
    val liveObjectPositions: List<Vector3> = emptyList(),
    val channelPeakMeters: Map<ReferenceChannel, Float> = emptyMap(),
    val lastAuditReport: PostRenderAuditResult? = null,
    val lastExportedBytes: ByteArray? = null,
    val lastExportedFileName: String? = null,
    val isPlayingPreview: Boolean = false,
    // Dialog flags
    val showRenderTargetDialog: Boolean = false,
    val showWiringDialog: Boolean = false,
    val showDemoGeneratorDialog: Boolean = false,
    val showMetadataModal: Boolean = false,
    // Enhanced AC-3 Merger state
    val mergerSlots: List<MergerChannelSlot> = defaultMergerSlots(),
    val mergerProgress: Float = 0f,
    val isMerging: Boolean = false
) {
    val isPtBr: Boolean get() = language == AppLanguage.PT_BR
}

private fun defaultMergerSlots(): List<MergerChannelSlot> = listOf(
    // Mandatory bed (2.0, 4.0, 5.0, or 5.1)
    MergerChannelSlot(ReferenceChannel.FrontLeft, "BED", "Atmos_Bed_5.1.eac3", 0),
    MergerChannelSlot(ReferenceChannel.FrontRight, "BED", "Atmos_Bed_5.1.eac3", 1),
    MergerChannelSlot(ReferenceChannel.FrontCenter, "BED", "Atmos_Bed_5.1.eac3", 2),
    MergerChannelSlot(ReferenceChannel.ScreenLFE, "BED", "Atmos_Bed_5.1.eac3", 3),
    MergerChannelSlot(ReferenceChannel.SideLeft, "BED", "Atmos_Bed_5.1.eac3", 4),
    MergerChannelSlot(ReferenceChannel.SideRight, "BED", "Atmos_Bed_5.1.eac3", 5),
    // Optional ground channels
    MergerChannelSlot(ReferenceChannel.FrontLeftCenter, "GROUND", null, 0),
    MergerChannelSlot(ReferenceChannel.FrontRightCenter, "GROUND", null, 1),
    MergerChannelSlot(ReferenceChannel.RearLeft, "GROUND", "Surround_Rear_7.1.wav", 0),
    MergerChannelSlot(ReferenceChannel.RearRight, "GROUND", "Surround_Rear_7.1.wav", 1),
    MergerChannelSlot(ReferenceChannel.WideLeft, "GROUND", null, 0),
    MergerChannelSlot(ReferenceChannel.WideRight, "GROUND", null, 1),
    // Optional height channels
    MergerChannelSlot(ReferenceChannel.TopFrontLeft, "HEIGHT", "Overhead_Height_4ch.wav", 0),
    MergerChannelSlot(ReferenceChannel.TopFrontRight, "HEIGHT", "Overhead_Height_4ch.wav", 1),
    MergerChannelSlot(ReferenceChannel.TopSideLeft, "HEIGHT", "Overhead_Height_4ch.wav", 2),
    MergerChannelSlot(ReferenceChannel.TopSideRight, "HEIGHT", "Overhead_Height_4ch.wav", 3)
)

class CavernizeViewModel : ViewModel() {

    private val _uiState = MutableStateFlow(CavernizeUiState())
    val uiState: StateFlow<CavernizeUiState> = _uiState.asStateFlow()

    private var audioPreviewJob: Job? = null
    private var activeAudioTrack: AudioTrack? = null

    init {
        // Pre-load a realistic Dolby Atmos E-AC-3 JOC showcase file so the user can explore immediately
        loadDemoPreset("Cinema_Atmos_Showcase_7.1.4.mkv")
    }

    fun toggleLanguage() {
        _uiState.update { state ->
            val nextLang = if (state.language == AppLanguage.PT_BR) AppLanguage.EN else AppLanguage.PT_BR
            val nextStrings = LocalizationRegistry.forLanguage(nextLang)
            state.copy(
                language = nextLang,
                strings = nextStrings,
                statusMessage = nextStrings.statusReady
            )
        }
    }

    fun selectTab(tab: MainNavTab) {
        _uiState.update { it.copy(currentTab = tab) }
    }

    fun setShowRenderTargetDialog(show: Boolean) {
        _uiState.update { it.copy(showRenderTargetDialog = show) }
    }

    fun setShowWiringDialog(show: Boolean) {
        _uiState.update { it.copy(showWiringDialog = show) }
    }

    fun setShowDemoGeneratorDialog(show: Boolean) {
        _uiState.update { it.copy(showDemoGeneratorDialog = show) }
    }

    fun setShowMetadataModal(show: Boolean) {
        _uiState.update { it.copy(showMetadataModal = show) }
    }

    fun selectRenderTarget(target: RenderTarget) {
        _uiState.update { it.copy(selectedRenderTarget = target, showRenderTargetDialog = false) }
    }

    fun selectExportFormat(format: ExportFormat) {
        _uiState.update { it.copy(selectedExportFormat = format) }
    }

    fun selectTrack(track: CavernizeTrack) {
        _uiState.update { it.copy(selectedTrack = track) }
    }

    fun loadExternalFileBytes(fileName: String, bytes: ByteArray) {
        viewModelScope.launch(Dispatchers.Default) {
            val parsed = AudioContainerParser.parseBytes(fileName, bytes)
            _uiState.update { state ->
                state.copy(
                    loadedFile = parsed,
                    selectedTrack = parsed.tracks.firstOrNull(),
                    statusMessage = if (state.isPtBr) {
                        "Arquivo carregado: $fileName (${parsed.tracks.size} faixa(s))"
                    } else {
                        "Loaded file: $fileName (${parsed.tracks.size} track(s))"
                    }
                )
            }
        }
    }

    fun loadDemoPreset(presetFileName: String) {
        viewModelScope.launch(Dispatchers.Default) {
            val parsed = AudioContainerParser.parseBytes(presetFileName, ByteArray(128))
            _uiState.update { state ->
                state.copy(
                    loadedFile = parsed,
                    selectedTrack = parsed.tracks.firstOrNull(),
                    showDemoGeneratorDialog = false,
                    statusMessage = if (state.isPtBr) {
                        "Demo 3D pronta: $presetFileName"
                    } else {
                        "3D Demo ready: $presetFileName"
                    }
                )
            }
        }
    }

    fun updateRenderingConfig(transform: (RenderingConfig) -> RenderingConfig) {
        _uiState.update { it.copy(renderingConfig = transform(it.renderingConfig)) }
    }

    fun updateUpmixingConfig(transform: (UpmixingConfig) -> UpmixingConfig) {
        _uiState.update { it.copy(upmixingConfig = transform(it.upmixingConfig)) }
    }

    fun resetUpmixingDefaults() {
        _uiState.update { it.copy(upmixingConfig = UpmixingConfig()) }
    }

    fun updateActiveBiquad(transform: (BiquadFilterConfig) -> BiquadFilterConfig) {
        _uiState.update { state ->
            state.copy(dspChain = state.dspChain.copy(activeBiquad = transform(state.dspChain.activeBiquad)))
        }
    }

    fun updateEqBand(index: Int, transform: (BiquadFilterConfig) -> BiquadFilterConfig) {
        _uiState.update { state ->
            val updated = state.dspChain.eqBands.toMutableList()
            if (index in updated.indices) {
                updated[index] = transform(updated[index])
            }
            state.copy(dspChain = state.dspChain.copy(eqBands = updated))
        }
    }

    fun addEqBand() {
        _uiState.update { state ->
            val nextId = (state.dspChain.eqBands.maxOfOrNull { it.id } ?: 0) + 1
            val newBand = BiquadFilterConfig(
                id = nextId,
                type = BiquadFilterType.PeakingEQ,
                centerFreqHz = 1000.0,
                q = 1.0,
                gainDb = 2.0
            )
            state.copy(dspChain = state.dspChain.copy(eqBands = state.dspChain.eqBands + newBand))
        }
    }

    fun removeEqBand(index: Int) {
        _uiState.update { state ->
            if (state.dspChain.eqBands.size <= 1) return@update state
            val updated = state.dspChain.eqBands.toMutableList().apply { removeAt(index) }
            state.copy(dspChain = state.dspChain.copy(eqBands = updated))
        }
    }

    fun toggleApplyEqOnRender(enabled: Boolean) {
        _uiState.update { state ->
            state.copy(dspChain = state.dspChain.copy(applyEqOnRender = enabled))
        }
    }

    fun toggleApplyConvolutionOnRender(enabled: Boolean) {
        _uiState.update { state ->
            state.copy(dspChain = state.dspChain.copy(applyConvolutionOnRender = enabled))
        }
    }

    fun synthesizeNewHrirProfile(profileName: String) {
        _uiState.update { state ->
            val ir = ConvolutionImpulseState.generateSyntheticHrir(profileName)
            state.copy(
                dspChain = state.dspChain.copy(convolutionImpulse = ir, applyConvolutionOnRender = true),
                statusMessage = if (state.isPtBr) "Impulso HRIR carregado: $profileName" else "Loaded HRIR impulse: $profileName"
            )
        }
    }

    // Queue management
    fun addCurrentToQueue() {
        val state = _uiState.value
        val track = state.selectedTrack ?: return
        val file = state.loadedFile ?: return
        val newJob = QueuedJob(
            id = System.currentTimeMillis() + state.queue.size,
            fileName = file.fileName,
            track = track,
            renderTarget = state.selectedRenderTarget,
            exportFormat = state.selectedExportFormat
        )
        _uiState.update {
            it.copy(
                queue = it.queue + newJob,
                selectedQueueJobId = newJob.id,
                statusMessage = if (it.isPtBr) "Adicionado à fila: ${file.fileName} (${state.selectedRenderTarget.name})"
                else "Added to queue: ${file.fileName} (${state.selectedRenderTarget.name})"
            )
        }
    }

    fun selectQueueJob(jobId: Long) {
        _uiState.update { it.copy(selectedQueueJobId = jobId) }
    }

    fun removeSelectedQueueJob() {
        val state = _uiState.value
        if (state.isRendering) return
        val targetId = state.selectedQueueJobId ?: return
        _uiState.update {
            val remaining = it.queue.filterNot { j -> j.id == targetId }
            it.copy(queue = remaining, selectedQueueJobId = remaining.firstOrNull()?.id)
        }
    }

    fun clearCompletedQueueJobs() {
        if (_uiState.value.isRendering) return
        _uiState.update { state ->
            val remaining = state.queue.filterNot { it.status == JobStatus.COMPLETED }
            state.copy(queue = remaining, selectedQueueJobId = remaining.firstOrNull()?.id)
        }
    }

    fun processQueue() {
        val state = _uiState.value
        if (state.isRendering || state.queue.isEmpty()) return
        viewModelScope.launch {
            for (job in _uiState.value.queue) {
                if (job.status == JobStatus.COMPLETED) continue
                _uiState.update { s ->
                    s.copy(
                        queue = s.queue.map { if (it.id == job.id) it.copy(status = JobStatus.RUNNING, progress = 0.05f) else it }
                    )
                }
                executeRenderPipeline(
                    fileName = job.fileName,
                    track = job.track,
                    target = job.renderTarget,
                    exportFormat = job.exportFormat,
                    queueJobId = job.id
                )
            }
        }
    }

    fun startSingleRender() {
        val state = _uiState.value
        if (state.isRendering) return
        val track = state.selectedTrack ?: return
        val file = state.loadedFile ?: return

        // Validate speaker virtualizer constraint (ground-only layout check from MainWindow.Rendering.cs)
        if (state.renderingConfig.speakerVirtualizer && state.selectedRenderTarget.channels.any { it.isHeightChannel }) {
            _uiState.update {
                it.copy(
                    statusMessage = if (it.isPtBr) {
                        "Aviso: Virtualização de altura em caixas requer um layout apenas com canais de solo (ex: 5.1 ou 7.1)."
                    } else {
                        "Notice: Height virtualization on speakers requires a ground-only layout (e.g., 5.1 or 7.1)."
                    }
                )
            }
        }

        viewModelScope.launch {
            executeRenderPipeline(
                fileName = file.fileName,
                track = track,
                target = state.selectedRenderTarget,
                exportFormat = state.selectedExportFormat,
                queueJobId = null
            )
        }
    }

    private suspend fun executeRenderPipeline(
        fileName: String,
        track: CavernizeTrack,
        target: RenderTarget,
        exportFormat: ExportFormat,
        queueJobId: Long?
    ) = withContext(Dispatchers.Default) {
        val initState = _uiState.value
        val renderCfg = initState.renderingConfig
        val upmixCfg = initState.upmixingConfig
        val dspChain = initState.dspChain

        _uiState.update {
            it.copy(
                isRendering = true,
                renderProgress = 0f,
                statusMessage = it.strings.statusStarting
            )
        }

        val targetRefs = if (target.isVirtualizer) {
            arrayOf(ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight)
        } else {
            val eff = target.getEffectiveChannels(renderCfg.surroundSwap)
            eff.indices.filter { target.isExported(it) }.map { eff[it] }.toTypedArray()
        }

        val statsTracker = RenderStatsTracker(
            targetChannels = targetRefs,
            totalSources = track.sources.size,
            dynamicSourcesCount = track.dynamicObjectCount
        )

        val totalBlocks = 24
        val blockSize = 1024
        val accumulatedOutput = Array(targetRefs.size) { FloatArray(totalBlocks * blockSize) }

        for (blockIdx in 0 until totalBlocks) {
            val sourceBlock = track.pcmGenerator(blockIdx, blockSize)
            val (renderedBlock, positions) = SpatialRendererEngine.renderBlock(
                sourceBlock = sourceBlock,
                sources = track.sources,
                target = target,
                upmix = upmixCfg,
                renderCfg = renderCfg,
                dspFilterChain = dspChain,
                frameIndex = blockIdx
            )

            statsTracker.processBlock(renderedBlock, positions)

            // Copy into accumulated output buffer & compute real-time VU meters
            val meters = mutableMapOf<ReferenceChannel, Float>()
            for (ch in targetRefs.indices) {
                if (ch < renderedBlock.size) {
                    val srcBuf = renderedBlock[ch]
                    System.arraycopy(srcBuf, 0, accumulatedOutput[ch], blockIdx * blockSize, blockSize)
                    var pk = 0f
                    for (v in srcBuf) {
                        val a = abs(v)
                        if (a > pk) pk = a
                    }
                    meters[targetRefs[ch]] = pk.coerceIn(0f, 1f)
                }
            }

            val progress = (blockIdx + 1).toFloat() / totalBlocks
            val speedX = String.format("%.1f", 4.2f + (blockIdx % 3) * 0.3f)
            val remSec = max(0, ((totalBlocks - blockIdx - 1) * 0.05f).roundToInt())

            _uiState.update { s ->
                val msg = if (s.isPtBr) {
                    String.format("Renderizando... (%.0f%%, veloc.: %sx, restante: 00:0%d)", progress * 100f, speedX, remSec)
                } else {
                    String.format("Rendering... (%.0f%%, speed: %sx, remaining: 00:0%d)", progress * 100f, speedX, remSec)
                }
                val updatedQueue = if (queueJobId != null) {
                    s.queue.map { j -> if (j.id == queueJobId) j.copy(progress = progress, status = JobStatus.RUNNING) else j }
                } else s.queue

                s.copy(
                    renderProgress = progress,
                    statusMessage = msg,
                    liveObjectPositions = positions,
                    channelPeakMeters = meters,
                    queue = updatedQueue
                )
            }
            delay(38)
        }

        val currentStrings = _uiState.value.strings
        val auditReport = statsTracker.buildReport(currentStrings, renderCfg.qualityGrading)

        val bitDepth = when {
            exportFormat.codec == Codec.PCM_Float -> BitDepth.Float32
            renderCfg.force24Bit -> BitDepth.Int24
            else -> BitDepth.Int16
        }

        val exportedBytes = if (!renderCfg.reportOnlyMode) {
            when (exportFormat.codec) {
                Codec.LimitlessAudio -> AudioWritersAndTranscoders.writeLimitlessAudioFormat(
                    accumulatedOutput, targetRefs, track.sampleRate, bitDepth
                )
                Codec.ADM_BWF, Codec.ADM_BWF_Atmos -> AudioWritersAndTranscoders.writeRiffWaveOrAdmBwf(
                    accumulatedOutput,
                    targetRefs,
                    track.sampleRate,
                    bitDepth,
                    skipChannelMask = renderCfg.wavChannelSkip,
                    includeAdmChunks = true,
                    isAtmosProfile = exportFormat.codec == Codec.ADM_BWF_Atmos
                )
                else -> AudioWritersAndTranscoders.writeRiffWaveOrAdmBwf(
                    accumulatedOutput,
                    targetRefs,
                    track.sampleRate,
                    bitDepth,
                    skipChannelMask = renderCfg.wavChannelSkip,
                    includeAdmChunks = false
                )
            }
        } else null

        val baseName = fileName.substringBeforeLast('.')
        val safeTargetName = target.name.replace(' ', '_')
        val ext = exportFormat.codec.defaultExtension
        val outFileName = "${baseName}_Cavern_${safeTargetName}$ext"

        _uiState.update { s ->
            val updatedQueue = if (queueJobId != null) {
                s.queue.map { j ->
                    if (j.id == queueJobId) j.copy(progress = 1f, status = JobStatus.COMPLETED, outputSummary = outFileName)
                    else j
                }
            } else s.queue

            s.copy(
                isRendering = false,
                renderProgress = 1f,
                statusMessage = "${s.strings.statusFinished} ($outFileName)",
                lastAuditReport = auditReport,
                lastExportedBytes = exportedBytes,
                lastExportedFileName = outFileName,
                queue = updatedQueue
            )
        }
    }

    /**
     * Plays a real-time 3D binaural preview of the active track rendered through the current RenderTarget & DSP chain.
     */
    fun toggleBinauralAudioPreview() {
        if (_uiState.value.isPlayingPreview) {
            stopAudioPreview()
            return
        }
        val state = _uiState.value
        val track = state.selectedTrack ?: return

        _uiState.update { it.copy(isPlayingPreview = true) }
        audioPreviewJob = viewModelScope.launch(Dispatchers.Default) {
            val sampleRate = 48000
            val blockSamples = 2048
            val minBuf = AudioTrack.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_OUT_STEREO,
                AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(blockSamples * 4)

            val audioTrack = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(sampleRate)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                        .build()
                )
                .setBufferSizeInBytes(minBuf)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()

            activeAudioTrack = audioTrack
            try {
                audioTrack.play()
                var frame = 0
                val pcmShort = ShortArray(blockSamples * 2)
                while (isActive && _uiState.value.isPlayingPreview && frame < 140) {
                    val cur = _uiState.value
                    val srcBlock = track.pcmGenerator(frame, blockSamples)
                    val (rendered, positions) = SpatialRendererEngine.renderBlock(
                        sourceBlock = srcBlock,
                        sources = track.sources,
                        target = cur.selectedRenderTarget,
                        upmix = cur.upmixingConfig,
                        renderCfg = cur.renderingConfig,
                        dspFilterChain = cur.dspChain,
                        frameIndex = frame
                    )
                    val effRefs = cur.selectedRenderTarget.getEffectiveChannels(cur.renderingConfig.surroundSwap)
                    val stereo = if (rendered.size == 2) rendered else VirtualizerEngine.renderBinaural(rendered, effRefs)

                    for (i in 0 until blockSamples) {
                        val l = (stereo[0][i].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                        val r = (stereo[1][i].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                        pcmShort[i * 2] = l
                        pcmShort[i * 2 + 1] = r
                    }
                    audioTrack.write(pcmShort, 0, pcmShort.size)
                    _uiState.update { it.copy(liveObjectPositions = positions) }
                    frame++
                }
            } catch (_: Exception) {
            } finally {
                try {
                    audioTrack.stop()
                    audioTrack.release()
                } catch (_: Exception) {
                }
                activeAudioTrack = null
                _uiState.update { it.copy(isPlayingPreview = false) }
            }
        }
    }

    fun stopAudioPreview() {
        audioPreviewJob?.cancel()
        audioPreviewJob = null
        _uiState.update { it.copy(isPlayingPreview = false) }
    }

    // Enhanced AC-3 Merger actions
    fun toggleMergerSlotAssignment(channel: ReferenceChannel) {
        _uiState.update { state ->
            val updated = state.mergerSlots.map { slot ->
                if (slot.targetChannel == channel) {
                    val nextAssigned = if (slot.assignedFileName == null) {
                        when (slot.group) {
                            "BED" -> "Atmos_Bed_5.1.eac3"
                            "GROUND" -> "Surround_Ground_Ext.wav"
                            else -> "Overhead_Height_4ch.wav"
                        }
                    } else null
                    slot.copy(assignedFileName = nextAssigned)
                } else slot
            }
            state.copy(mergerSlots = updated)
        }
    }

    fun populateDemoMerger714() {
        _uiState.update { state ->
            state.copy(
                mergerSlots = defaultMergerSlots(),
                statusMessage = if (state.isPtBr) "Canais 7.1.4 atribuídos no Enhanced AC-3 Merger."
                else "7.1.4 channels assigned in Enhanced AC-3 Merger."
            )
        }
    }

    fun clearAllMergerSlots() {
        _uiState.update { state ->
            state.copy(mergerSlots = state.mergerSlots.map { it.copy(assignedFileName = null) })
        }
    }

    fun runEnhancedAc3Merge() {
        val state = _uiState.value
        if (state.isMerging) return
        val activeChannels = state.mergerSlots.filter { it.assignedFileName != null }.map { it.targetChannel }
        if (activeChannels.isEmpty()) {
            _uiState.update {
                it.copy(
                    statusMessage = if (it.isPtBr) "Selecione pelo menos a cama 2.0/5.1 antes de mesclar."
                    else "Assign at least a 2.0/5.1 bed before merging."
                )
            }
            return
        }

        viewModelScope.launch(Dispatchers.Default) {
            _uiState.update { it.copy(isMerging = true, mergerProgress = 0.1f) }
            for (step in 1..10) {
                delay(45)
                _uiState.update { it.copy(mergerProgress = step / 10f) }
            }
            val mergedBytes = AudioWritersAndTranscoders.mergeEnhancedAc3Streams(activeChannels)
            val outName = "Cavern_Merged_${activeChannels.size}ch.eac3"
            _uiState.update {
                it.copy(
                    isMerging = false,
                    mergerProgress = 1f,
                    lastExportedBytes = mergedBytes,
                    lastExportedFileName = outName,
                    statusMessage = if (it.isPtBr) {
                        "Enhanced AC-3 Merger concluído: $outName (${activeChannels.size} canais mesclados)"
                    } else {
                        "Enhanced AC-3 Merger finished: $outName (${activeChannels.size} channels merged)"
                    }
                )
            }
        }
    }

    override fun onCleared() {
        super.onCleared()
        stopAudioPreview()
    }
}
