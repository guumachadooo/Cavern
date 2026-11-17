package hu.sbence.cavernize.ui.screens

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Assessment
import androidx.compose.material.icons.filled.CallMerge
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Equalizer
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SaveAlt
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import hu.sbence.cavernize.cavern.core.BiquadFilterType
import hu.sbence.cavernize.cavern.core.ChannelPrototype
import hu.sbence.cavernize.ui.CavernizeUiState
import hu.sbence.cavernize.ui.CavernizeViewModel
import hu.sbence.cavernize.ui.MergerChannelSlot
import hu.sbence.cavernize.ui.components.FrequencyResponseGraphCanvas
import hu.sbence.cavernize.ui.theme.AtmosDynamicAmber
import hu.sbence.cavernize.ui.theme.CavernCyan
import hu.sbence.cavernize.ui.theme.JetBrainsMonoFamily
import hu.sbence.cavernize.ui.theme.SpeakerActiveEmerald
import hu.sbence.cavernize.ui.theme.StudioBorder
import hu.sbence.cavernize.ui.theme.StudioCard
import hu.sbence.cavernize.ui.theme.StudioObsidian
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.roundToInt

/**
 * Tab 2: Port of UpmixingSetup.xaml, BiquadEditor.xaml, EQEditor.xaml, and ConvolutionEditor.xaml.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun DspAndUpmixScreen(
    state: CavernizeUiState,
    viewModel: CavernizeViewModel,
    onExportEqualizerApoTxt: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val strings = state.strings
    val scrollState = rememberScrollState()
    var showSpectrum by remember { mutableStateOf(true) }
    var showPhase by remember { mutableStateOf(true) }
    var graphSingleBiquadMode by remember { mutableStateOf(true) }

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // 1. UPMIXING SETUP CARD (Port of Cavern.WPF.UpmixingSetup)
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(Icons.Default.Tune, contentDescription = null, tint = CavernCyan)
                        Text(strings.upmixSetupTitle, style = MaterialTheme.typography.titleMedium)
                    }
                    OutlinedButton(
                        onClick = { viewModel.resetUpmixingDefaults() },
                        modifier = Modifier.testTag("reset_upmix_button")
                    ) {
                        Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text(strings.resetDefaults)
                    }
                }

                val upmix = state.upmixingConfig
                OptionToggleRow(
                    title = strings.matrixUpmix,
                    subtitle = strings.matrixUpmixDesc,
                    checked = upmix.matrixUpmix,
                    onCheckedChange = { v -> viewModel.updateUpmixingConfig { it.copy(matrixUpmix = v) } }
                )
                OptionToggleRow(
                    title = strings.cavernizeUpmix,
                    subtitle = strings.cavernizeUpmixDesc,
                    checked = upmix.cavernizeHeight,
                    onCheckedChange = { v -> viewModel.updateUpmixingConfig { it.copy(cavernizeHeight = v) } }
                )

                // Effect Strength Slider (0..100%)
                Column {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(strings.effectStrength, style = MaterialTheme.typography.bodySmall)
                        Text(
                            text = "${(upmix.effectStrength * 100).roundToInt()}%",
                            style = MaterialTheme.typography.labelMedium,
                            color = CavernCyan
                        )
                    }
                    Slider(
                        value = upmix.effectStrength,
                        onValueChange = { v -> viewModel.updateUpmixingConfig { it.copy(effectStrength = v) } },
                        valueRange = 0f..1f
                    )
                }

                // Smoothness Slider (0..100%)
                Column {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(strings.smoothness, style = MaterialTheme.typography.bodySmall)
                        Text(
                            text = "${(upmix.smoothness * 100).roundToInt()}%",
                            style = MaterialTheme.typography.labelMedium,
                            color = SpeakerActiveEmerald
                        )
                    }
                    Slider(
                        value = upmix.smoothness,
                        onValueChange = { v -> viewModel.updateUpmixingConfig { it.copy(smoothness = v) } },
                        valueRange = 0f..1f
                    )
                }
            }
        }

        // 2. BIQUAD & PARAMETRIC EQ EDITOR CARD (Port of BiquadEditor.xaml & EQEditor.xaml)
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(Icons.Default.Equalizer, contentDescription = null, tint = SpeakerActiveEmerald)
                        Text(
                            text = if (graphSingleBiquadMode) strings.biquadEditorTitle else strings.eqEditorTitle,
                            style = MaterialTheme.typography.titleMedium
                        )
                    }
                    Switch(
                        checked = state.dspChain.applyEqOnRender,
                        onCheckedChange = { viewModel.toggleApplyEqOnRender(it) }
                    )
                }

                // Mode Switch: Single Biquad Inspector vs Multi-Band QuickEQ
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(
                        selected = graphSingleBiquadMode,
                        onClick = { graphSingleBiquadMode = true },
                        label = { Text("Biquad Filter") }
                    )
                    FilterChip(
                        selected = !graphSingleBiquadMode,
                        onClick = { graphSingleBiquadMode = false },
                        label = { Text("Multi-Band EQ (${state.dspChain.eqBands.size})") }
                    )
                }

                val curvePoints = remember(state.dspChain, graphSingleBiquadMode) {
                    state.dspChain.evaluateCompositeCurve(includeSingleBiquad = graphSingleBiquadMode)
                }

                FrequencyResponseGraphCanvas(
                    curvePoints = curvePoints,
                    showSpectrum = showSpectrum,
                    showPhase = showPhase
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    FilterChip(
                        selected = showSpectrum,
                        onClick = { showSpectrum = !showSpectrum },
                        label = { Text(strings.showSpectrum) }
                    )
                    FilterChip(
                        selected = showPhase,
                        onClick = { showPhase = !showPhase },
                        label = { Text(strings.showPhase) }
                    )
                }

                if (graphSingleBiquadMode) {
                    val bq = state.dspChain.activeBiquad
                    Text(strings.filterType, style = MaterialTheme.typography.labelLarge, color = CavernCyan)
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        verticalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        BiquadFilterType.entries.forEach { ft ->
                            FilterChip(
                                selected = bq.type == ft,
                                onClick = { viewModel.updateActiveBiquad { it.copy(type = ft) } },
                                label = { Text(ft.eqApoCode) }
                            )
                        }
                    }

                    // Center Frequency logarithmic slider (20 Hz .. 20,000 Hz)
                    val logMin = ln(20f)
                    val logMax = ln(20000f)
                    val freqNorm = ((ln(bq.centerFreqHz.toFloat().coerceIn(20f, 20000f)) - logMin) / (logMax - logMin)).coerceIn(0f, 1f)
                    Column {
                        Row(Modifier.fillMaxWidth(), Arrangement.SpaceBetween) {
                            Text(strings.centerFreqHz, style = MaterialTheme.typography.bodySmall)
                            Text(String.format("%.1f Hz", bq.centerFreqHz), style = MaterialTheme.typography.labelMedium, color = CavernCyan)
                        }
                        Slider(
                            value = freqNorm,
                            onValueChange = { norm ->
                                val newFreq = exp(logMin + norm * (logMax - logMin)).toDouble()
                                viewModel.updateActiveBiquad { it.copy(centerFreqHz = newFreq) }
                            }
                        )
                    }

                    // Q Factor slider (0.2 .. 12.0)
                    Column {
                        Row(Modifier.fillMaxWidth(), Arrangement.SpaceBetween) {
                            Text(strings.qFactor, style = MaterialTheme.typography.bodySmall)
                            Text(String.format("Q = %.3f", bq.q), style = MaterialTheme.typography.labelMedium, color = SpeakerActiveEmerald)
                        }
                        Slider(
                            value = bq.q.toFloat().coerceIn(0.2f, 12f),
                            onValueChange = { qv -> viewModel.updateActiveBiquad { it.copy(q = qv.toDouble()) } },
                            valueRange = 0.2f..12f
                        )
                    }

                    // Gain dB slider (-24 .. +24 dB)
                    Column {
                        Row(Modifier.fillMaxWidth(), Arrangement.SpaceBetween) {
                            Text(strings.gainDb, style = MaterialTheme.typography.bodySmall)
                            Text(String.format("%+.1f dB", bq.gainDb), style = MaterialTheme.typography.labelMedium, color = AtmosDynamicAmber)
                        }
                        Slider(
                            value = bq.gainDb.toFloat().coerceIn(-24f, 24f),
                            onValueChange = { gv -> viewModel.updateActiveBiquad { it.copy(gainDb = gv.toDouble()) } },
                            valueRange = -24f..24f
                        )
                    }

                    OptionToggleRow(
                        title = strings.swapPhase,
                        subtitle = bq.toEqualizerApoLine(1),
                        checked = bq.swapPhase,
                        onCheckedChange = { sw -> viewModel.updateActiveBiquad { it.copy(swapPhase = sw) } }
                    )
                } else {
                    // Multi-Band Parametric EQ list
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        state.dspChain.eqBands.forEachIndexed { idx, band ->
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(10.dp))
                                    .background(StudioObsidian)
                                    .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                                    .padding(10.dp)
                            ) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        text = "Band #${idx + 1}: ${band.type.eqApoCode} @ ${band.centerFreqHz.roundToInt()} Hz (${String.format("%+.1f dB", band.gainDb)})",
                                        style = MaterialTheme.typography.labelLarge,
                                        color = CavernCyan
                                    )
                                    IconButton(
                                        onClick = { viewModel.removeEqBand(idx) },
                                        modifier = Modifier.size(28.dp)
                                    ) {
                                        Icon(Icons.Default.Delete, contentDescription = null, modifier = Modifier.size(16.dp))
                                    }
                                }
                                Slider(
                                    value = band.gainDb.toFloat().coerceIn(-18f, 18f),
                                    onValueChange = { g -> viewModel.updateEqBand(idx) { it.copy(gainDb = g.toDouble()) } },
                                    valueRange = -18f..18f
                                )
                            }
                        }

                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            OutlinedButton(
                                onClick = { viewModel.addEqBand() },
                                modifier = Modifier.weight(1f)
                            ) {
                                Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                                Spacer(Modifier.width(4.dp))
                                Text(strings.addBand)
                            }
                            FilledTonalButton(
                                onClick = { onExportEqualizerApoTxt(state.dspChain.exportEqualizerApoConfig()) },
                                modifier = Modifier.weight(1f)
                            ) {
                                Icon(Icons.Default.SaveAlt, contentDescription = null, modifier = Modifier.size(18.dp))
                                Spacer(Modifier.width(4.dp))
                                Text(strings.exportEqualizerApo)
                            }
                        }
                    }
                }
            }
        }

        // 3. CONVOLUTION & HRIR IMPULSE EDITOR CARD (Port of ConvolutionEditor.xaml)
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                val conv = state.dspChain.convolutionImpulse
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(Icons.Default.GraphicEq, contentDescription = null, tint = AtmosDynamicAmber)
                        Text(strings.convolutionEditorTitle, style = MaterialTheme.typography.titleMedium)
                    }
                    Switch(
                        checked = state.dspChain.applyConvolutionOnRender,
                        onCheckedChange = { viewModel.toggleApplyConvolutionOnRender(it) }
                    )
                }

                Text(
                    text = conv.name,
                    style = MaterialTheme.typography.labelLarge,
                    color = CavernCyan
                )

                // Impulse Time-Domain Waveform Canvas
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(105.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(StudioObsidian)
                        .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                        .padding(8.dp)
                ) {
                    Canvas(modifier = Modifier.fillMaxSize()) {
                        val w = size.width
                        val h = size.height
                        val midY = h * 0.5f
                        drawLine(Color(0xFF334155), Offset(0f, midY), Offset(w, midY), 1f)
                        val ir = conv.samples
                        if (ir.size > 1) {
                            val path = Path()
                            for (i in ir.indices) {
                                val x = (i.toFloat() / (ir.size - 1)) * w
                                val y = (midY - ir[i] * midY * 0.9f).coerceIn(0f, h)
                                if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
                            }
                            drawPath(path, SpeakerActiveEmerald, style = Stroke(width = 2.2f))
                        }
                    }
                }

                // Impulse telemetry metrics (Polarity, Phase, Delay)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text(
                        text = "${strings.impulsePolarity}: ${if (conv.isPositivePolarity) "Positiva (+)" else "Invertida (-)"}",
                        style = MaterialTheme.typography.labelMedium
                    )
                    Text(
                        text = "${strings.impulsePhase}: ${String.format("%.1f°", conv.averagePhaseDegrees)}",
                        style = MaterialTheme.typography.labelMedium
                    )
                    Text(
                        text = "${strings.impulseDelay}: ${String.format("%.2f ms", conv.delayMs)}",
                        style = MaterialTheme.typography.labelMedium
                    )
                }

                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    FilledTonalButton(
                        onClick = { viewModel.synthesizeNewHrirProfile("KEMAR Diffuse-Field Binaural HRIR (48 kHz)") }
                    ) {
                        Text("KEMAR HRIR 48kHz")
                    }
                    FilledTonalButton(
                        onClick = { viewModel.synthesizeNewHrirProfile("Cavern Cinema Room QuickEQ FIR (48 kHz)") }
                    ) {
                        Text("QuickEQ Cinema FIR")
                    }
                }
            }
        }
    }
}

/**
 * Tab 3: Port of EnhancedAC3Merger/MainWindow.xaml & MainWindow.Process.cs.
 */
@Composable
fun EnhancedAc3MergerScreen(
    state: CavernizeUiState,
    viewModel: CavernizeViewModel,
    onSaveMergedFile: () -> Unit,
    modifier: Modifier = Modifier
) {
    val strings = state.strings
    val scrollState = rememberScrollState()

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Icon(Icons.Default.CallMerge, contentDescription = null, tint = CavernCyan)
                    Text(strings.mergerTitle, style = MaterialTheme.typography.titleMedium)
                }
                Text(
                    text = strings.mergerSubtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    FilledTonalButton(
                        onClick = { viewModel.populateDemoMerger714() },
                        modifier = Modifier.weight(1f).testTag("merger_populate_demo_button")
                    ) {
                        Text(strings.assignDemoBedAndHeight)
                    }
                    OutlinedButton(
                        onClick = { viewModel.clearAllMergerSlots() },
                        modifier = Modifier.weight(1f).testTag("merger_clear_button")
                    ) {
                        Text(strings.clearAllChannels)
                    }
                }
            }
        }

        // Group 1: Mandatory bed (2.0, 4.0, 5.0, or 5.1)
        MergerChannelGroupCard(
            title = strings.mandatoryBedHeader,
            slots = state.mergerSlots.filter { it.group == "BED" },
            unassignedLabel = strings.channelUnassigned,
            onToggleChannel = { viewModel.toggleMergerSlotAssignment(it.targetChannel) }
        )

        // Group 2: Optional ground channels (FLC, FRC, RL, RR, WL, WR)
        MergerChannelGroupCard(
            title = strings.optionalGroundHeader,
            slots = state.mergerSlots.filter { it.group == "GROUND" },
            unassignedLabel = strings.channelUnassigned,
            onToggleChannel = { viewModel.toggleMergerSlotAssignment(it.targetChannel) }
        )

        // Group 3: Optional height channels (TFL, TFR, TSL, TSR)
        MergerChannelGroupCard(
            title = strings.optionalHeightHeader,
            slots = state.mergerSlots.filter { it.group == "HEIGHT" },
            unassignedLabel = strings.channelUnassigned,
            onToggleChannel = { viewModel.toggleMergerSlotAssignment(it.targetChannel) }
        )

        if (state.isMerging) {
            LinearProgressIndicator(
                progress = { state.mergerProgress },
                modifier = Modifier.fillMaxWidth(),
                color = CavernCyan
            )
        }

        val activeCount = state.mergerSlots.count { it.assignedFileName != null }
        Button(
            onClick = { viewModel.runEnhancedAc3Merge() },
            enabled = !state.isMerging && activeCount > 0,
            colors = ButtonDefaults.buttonColors(containerColor = SpeakerActiveEmerald),
            modifier = Modifier
                .fillMaxWidth()
                .height(50.dp)
                .testTag("merge_eac3_streams_button")
        ) {
            Icon(Icons.Default.CallMerge, contentDescription = null)
            Spacer(Modifier.width(8.dp))
            Text("${strings.mergeStreamsBtn} ($activeCount ch)", fontWeight = FontWeight.Bold)
        }

        if (state.lastExportedFileName?.endsWith(".eac3") == true && state.lastExportedBytes != null) {
            Button(
                onClick = onSaveMergedFile,
                colors = ButtonDefaults.buttonColors(containerColor = CavernCyan),
                modifier = Modifier.fillMaxWidth().height(48.dp)
            ) {
                Icon(Icons.Default.SaveAlt, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("${strings.saveExportFile}: ${state.lastExportedFileName}")
            }
        }
    }
}

@Composable
private fun MergerChannelGroupCard(
    title: String,
    slots: List<MergerChannelSlot>,
    unassignedLabel: String,
    onToggleChannel: (MergerChannelSlot) -> Unit
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        shape = RoundedCornerShape(16.dp),
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = CavernCyan)
            slots.forEach { slot ->
                val proto = ChannelPrototype.fromReference(slot.targetChannel)
                val assigned = slot.assignedFileName != null
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(if (assigned) StudioCard else StudioObsidian)
                        .border(
                            1.dp,
                            if (assigned) SpeakerActiveEmerald.copy(alpha = 0.7f) else StudioBorder,
                            RoundedCornerShape(10.dp)
                        )
                        .clickable { onToggleChannel(slot) }
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "${slot.targetChannel.shortMark} — ${proto.name}",
                            style = MaterialTheme.typography.bodyMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = if (assigned) SpeakerActiveEmerald else MaterialTheme.colorScheme.onSurface
                        )
                        Text(
                            text = if (assigned) "${slot.assignedFileName} [Ch #${slot.sourceChannelIndex + 1}]" else unassignedLabel,
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    if (assigned) {
                        Icon(
                            imageVector = Icons.Default.CheckCircle,
                            contentDescription = null,
                            tint = SpeakerActiveEmerald
                        )
                    }
                }
            }
        }
    }
}

/**
 * Tab 4: Post-Render Audit Report (Grades A+ to F), Codec Metadata Inspector, and Bilingual Guide.
 */
@Composable
fun ReportsAndInfoScreen(
    state: CavernizeUiState,
    modifier: Modifier = Modifier
) {
    val strings = state.strings
    val scrollState = rememberScrollState()

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // 1. POST-RENDER REPORT & QUALITY GRADING CARD
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Icon(Icons.Default.Assessment, contentDescription = null, tint = SpeakerActiveEmerald)
                    Text(strings.postRenderReportTitle, style = MaterialTheme.typography.titleMedium)
                }

                val report = state.lastAuditReport
                if (report == null) {
                    Text(
                        text = strings.defaultReportHint,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                } else {
                    // Grade Badges Row
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        GradePill("Macro", report.macrodynamicsGrade, Modifier.weight(1f))
                        GradePill("Micro", report.microdynamicsGrade, Modifier.weight(1f))
                        GradePill("Chest Slam", report.chestSlamGrade, Modifier.weight(1f))
                        GradePill("3D Height", report.heightGrade, Modifier.weight(1f))
                    }

                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .padding(12.dp)
                    ) {
                        Text(
                            text = report.formattedText,
                            style = MaterialTheme.typography.bodySmall.copy(fontFamily = JetBrainsMonoFamily),
                            color = Color(0xFFBAE6FD)
                        )
                    }
                }
            }
        }

        // 2. CODEC METADATA INSPECTOR (Port of Windows/CodecMetadata.xaml)
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Icon(Icons.Default.Info, contentDescription = null, tint = CavernCyan)
                    Text(strings.codecMetadataTitle, style = MaterialTheme.typography.titleMedium)
                }

                val meta = state.selectedTrack?.metadata
                if (meta == null) {
                    Text(
                        text = strings.noFileLoaded,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                } else {
                    meta.headers.forEach { section ->
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .background(StudioObsidian)
                                .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                                .padding(10.dp),
                            verticalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            Text(
                                text = section.title,
                                style = MaterialTheme.typography.titleSmall,
                                color = CavernCyan
                            )
                            HorizontalDivider(color = StudioBorder)
                            section.fields.forEach { field ->
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                    Text(
                                        text = field.name,
                                        style = MaterialTheme.typography.labelMedium,
                                        color = SpeakerActiveEmerald
                                    )
                                    Text(
                                        text = field.value,
                                        style = MaterialTheme.typography.labelMedium,
                                        color = Color.White
                                    )
                                }
                                if (field.description.isNotEmpty()) {
                                    Text(
                                        text = field.description,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun GradePill(label: String, grade: String, modifier: Modifier = Modifier) {
    val gradeColor = when (grade) {
        "A+", "A" -> SpeakerActiveEmerald
        "B", "C" -> CavernCyan
        else -> AtmosDynamicAmber
    }
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(10.dp))
            .background(StudioObsidian)
            .border(1.dp, gradeColor.copy(alpha = 0.6f), RoundedCornerShape(10.dp))
            .padding(vertical = 8.dp, horizontal = 6.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text(
            text = grade,
            style = MaterialTheme.typography.titleLarge,
            color = gradeColor,
            fontWeight = FontWeight.Bold
        )
        Text(
            text = label,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}
