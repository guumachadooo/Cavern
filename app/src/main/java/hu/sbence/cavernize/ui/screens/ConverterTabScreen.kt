package hu.sbence.cavernize.ui.screens

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
import androidx.compose.material.icons.filled.AddToQueue
import androidx.compose.material.icons.filled.AudioFile
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Cable
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PlaylistPlay
import androidx.compose.material.icons.filled.SaveAlt
import androidx.compose.material.icons.filled.SpeakerGroup
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import hu.sbence.cavernize.cavern.format.ExportFormat
import hu.sbence.cavernize.cavern.format.JobStatus
import hu.sbence.cavernize.ui.CavernizeUiState
import hu.sbence.cavernize.ui.CavernizeViewModel
import hu.sbence.cavernize.ui.components.StandardLayoutDisplayCanvas
import hu.sbence.cavernize.ui.theme.AtmosDynamicAmber
import hu.sbence.cavernize.ui.theme.CavernCyan
import hu.sbence.cavernize.ui.theme.JetBrainsMonoFamily
import hu.sbence.cavernize.ui.theme.SpeakerActiveEmerald
import hu.sbence.cavernize.ui.theme.StudioBorder
import hu.sbence.cavernize.ui.theme.StudioCard
import hu.sbence.cavernize.ui.theme.StudioObsidian

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ConverterTabScreen(
    state: CavernizeUiState,
    viewModel: CavernizeViewModel,
    onOpenSystemFilePicker: () -> Unit,
    onSaveExportedFile: () -> Unit,
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
        // 1. SYSTEM & RENDER TARGET PANEL (Port of renderSettings Grid in MainWindow.xaml)
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
                        Icon(
                            imageVector = Icons.Default.SpeakerGroup,
                            contentDescription = null,
                            tint = CavernCyan
                        )
                        Text(
                            text = strings.systemHeader,
                            style = MaterialTheme.typography.titleMedium,
                            color = MaterialTheme.colorScheme.onSurface
                        )
                    }
                    OutlinedButton(
                        onClick = { viewModel.setShowWiringDialog(true) },
                        enabled = !state.selectedRenderTarget.isDriverDefault,
                        modifier = Modifier.testTag("display_wiring_button")
                    ) {
                        Icon(
                            imageVector = Icons.Default.Cable,
                            contentDescription = null,
                            modifier = Modifier.size(16.dp)
                        )
                        Spacer(Modifier.width(6.dp))
                        Text(strings.displayWiring, style = MaterialTheme.typography.labelLarge)
                    }
                }

                Text(
                    text = strings.renderTargetInfo,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                // Render Target Selector Button (Opens RenderTargetSelector Window port)
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(StudioCard)
                        .border(1.dp, CavernCyan.copy(alpha = 0.5f), RoundedCornerShape(12.dp))
                        .clickable { viewModel.setShowRenderTargetDialog(true) }
                        .padding(horizontal = 14.dp, vertical = 12.dp)
                        .testTag("render_target_selector_button"),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column {
                        Text(
                            text = strings.renderTargetLabel,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                            text = "${state.selectedRenderTarget.name} (${state.selectedRenderTarget.outputChannels} ch)",
                            style = MaterialTheme.typography.titleMedium,
                            color = CavernCyan
                        )
                    }
                    Icon(
                        imageVector = Icons.Default.ExpandMore,
                        contentDescription = null,
                        tint = CavernCyan
                    )
                }

                // 3D Speaker Cube Layout Display
                StandardLayoutDisplayCanvas(
                    renderTarget = state.selectedRenderTarget,
                    surroundSwap = state.renderingConfig.surroundSwap,
                    liveObjects = state.liveObjectPositions,
                    channelPeaks = state.channelPeakMeters,
                    isPtBr = state.isPtBr
                )
            }
        }

        // 2. CONTENT & TRACK PROCESSING PANEL (Port of CoPro Grid in MainWindow.xaml)
        Card(
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier
                .fillMaxWidth()
                .border(1.dp, StudioBorder, RoundedCornerShape(16.dp))
        ) {
            Column(
                modifier = Modifier.padding(14.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
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
                        Icon(
                            imageVector = Icons.Default.AudioFile,
                            contentDescription = null,
                            tint = SpeakerActiveEmerald
                        )
                        Text(
                            text = strings.contentHeader,
                            style = MaterialTheme.typography.titleMedium
                        )
                    }
                    if (state.selectedTrack != null) {
                        OutlinedButton(
                            onClick = { viewModel.setShowMetadataModal(true) },
                            modifier = Modifier.testTag("show_metadata_button")
                        ) {
                            Icon(Icons.Default.Info, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(if (state.isPtBr) "Metadados" else "Metadata")
                        }
                    }
                }

                // File open & 3D Demo Generator Buttons
                FlowRow(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Button(
                        onClick = onOpenSystemFilePicker,
                        colors = ButtonDefaults.buttonColors(containerColor = CavernCyan),
                        modifier = Modifier.testTag("open_file_button")
                    ) {
                        Icon(Icons.Default.FolderOpen, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.openFile)
                    }

                    FilledTonalButton(
                        onClick = { viewModel.setShowDemoGeneratorDialog(true) },
                        modifier = Modifier.testTag("generate_demo_button")
                    ) {
                        Icon(Icons.Default.AutoAwesome, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.generateDemoFile)
                    }

                    if (state.selectedTrack != null) {
                        OutlinedButton(
                            onClick = { viewModel.toggleBinauralAudioPreview() },
                            modifier = Modifier.testTag("binaural_preview_button")
                        ) {
                            Icon(
                                imageVector = if (state.isPlayingPreview) Icons.Default.Stop else Icons.Default.PlayArrow,
                                contentDescription = null,
                                tint = if (state.isPlayingPreview) AtmosDynamicAmber else SpeakerActiveEmerald,
                                modifier = Modifier.size(18.dp)
                            )
                            Spacer(Modifier.width(6.dp))
                            Text(if (state.isPlayingPreview) strings.stopPreview else strings.playPreview)
                        }
                    }
                }

                // Loaded File Name Badge
                val file = state.loadedFile
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(StudioObsidian)
                        .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                        .padding(10.dp)
                ) {
                    if (file == null) {
                        Column {
                            Text(
                                text = strings.noFileLoaded,
                                style = MaterialTheme.typography.titleSmall,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Text(
                                text = strings.loadPromptHint,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    } else {
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(
                                text = file.fileName,
                                style = MaterialTheme.typography.titleSmall,
                                color = SpeakerActiveEmerald,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                text = "${file.container.displayName} • ${file.tracks.size} faixa(s)",
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }

                // Track Dropdown Selector
                if (file != null && file.tracks.isNotEmpty()) {
                    var trackMenuExpanded by remember { mutableStateOf(false) }
                    Box(modifier = Modifier.fillMaxWidth()) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .background(StudioCard)
                                .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                                .clickable { trackMenuExpanded = true }
                                .padding(12.dp)
                                .testTag("track_selector_dropdown"),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = strings.trackLabel,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Text(
                                    text = state.selectedTrack?.title ?: "",
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontWeight = FontWeight.SemiBold,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            }
                            Icon(Icons.Default.ExpandMore, contentDescription = null)
                        }
                        DropdownMenu(
                            expanded = trackMenuExpanded,
                            onDismissRequest = { trackMenuExpanded = false }
                        ) {
                            file.tracks.forEach { trk ->
                                DropdownMenuItem(
                                    text = { Text(trk.title) },
                                    onClick = {
                                        viewModel.selectTrack(trk)
                                        trackMenuExpanded = false
                                    }
                                )
                            }
                        }
                    }
                }

                // Output Export Format Selector
                var outputMenuExpanded by remember { mutableStateOf(false) }
                Box(modifier = Modifier.fillMaxWidth()) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioCard)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .clickable { outputMenuExpanded = true }
                            .padding(12.dp)
                            .testTag("export_format_dropdown"),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = strings.outputLabel,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Text(
                                text = state.selectedExportFormat.getDescription(state.isPtBr),
                                style = MaterialTheme.typography.bodyMedium,
                                fontWeight = FontWeight.SemiBold
                            )
                        }
                        Icon(Icons.Default.ExpandMore, contentDescription = null)
                    }
                    DropdownMenu(
                        expanded = outputMenuExpanded,
                        onDismissRequest = { outputMenuExpanded = false }
                    ) {
                        ExportFormat.AllFormats.forEach { fmt ->
                            DropdownMenuItem(
                                text = { Text(fmt.getDescription(state.isPtBr)) },
                                onClick = {
                                    viewModel.selectExportFormat(fmt)
                                    outputMenuExpanded = false
                                }
                            )
                        }
                    }
                }

                // TrackInfo UserControl port
                state.selectedTrack?.let { trk ->
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .padding(12.dp)
                    ) {
                        Text(
                            text = trk.getDetailedInfo(state.isPtBr),
                            style = MaterialTheme.typography.bodySmall.copy(fontFamily = JetBrainsMonoFamily),
                            color = Color(0xFFBAE6FD)
                        )
                    }
                }

                // Action Row: Add to Queue & Render Now
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    OutlinedButton(
                        onClick = { viewModel.addCurrentToQueue() },
                        enabled = state.selectedTrack != null && !state.isRendering,
                        modifier = Modifier
                            .weight(1f)
                            .height(48.dp)
                            .testTag("add_to_queue_button")
                    ) {
                        Icon(Icons.Default.AddToQueue, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.addToQueue)
                    }

                    Button(
                        onClick = { viewModel.startSingleRender() },
                        enabled = state.selectedTrack != null && !state.isRendering,
                        colors = ButtonDefaults.buttonColors(containerColor = SpeakerActiveEmerald),
                        modifier = Modifier
                            .weight(1f)
                            .height(48.dp)
                            .testTag("render_now_button")
                    ) {
                        Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.renderNow, fontWeight = FontWeight.Bold)
                    }
                }

                // Save Exported File Button if a render has completed
                if (state.lastExportedBytes != null && state.lastExportedFileName != null) {
                    Button(
                        onClick = onSaveExportedFile,
                        colors = ButtonDefaults.buttonColors(containerColor = CavernCyan),
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(48.dp)
                            .testTag("save_exported_file_button")
                    ) {
                        Icon(Icons.Default.SaveAlt, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("${strings.saveExportFile}: ${state.lastExportedFileName} (${state.lastExportedBytes.size / 1024} KB)")
                    }
                }
            }
        }

        // 3. RENDERING MENU OPTIONS CARD (Port of MenuItem flags in MainWindow.xaml)
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
                Text(
                    text = strings.renderingOptionsTitle,
                    style = MaterialTheme.typography.titleMedium,
                    color = CavernCyan
                )
                val cfg = state.renderingConfig

                OptionToggleRow(
                    title = strings.speakerVirtualizer,
                    subtitle = strings.speakerVirtualizerDesc,
                    checked = cfg.speakerVirtualizer,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(speakerVirtualizer = v) } }
                )
                HorizontalDivider(color = StudioBorder)
                OptionToggleRow(
                    title = strings.muteBed,
                    subtitle = strings.muteBedDesc,
                    checked = cfg.muteBed,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(muteBed = v) } }
                )
                OptionToggleRow(
                    title = strings.muteGround,
                    subtitle = strings.muteGroundDesc,
                    checked = cfg.muteGround,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(muteGround = v) } }
                )
                HorizontalDivider(color = StudioBorder)
                OptionToggleRow(
                    title = strings.force24Bit,
                    subtitle = strings.force24BitDesc,
                    checked = cfg.force24Bit,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(force24Bit = v) } }
                )
                OptionToggleRow(
                    title = strings.surroundSwap,
                    subtitle = strings.surroundSwapDesc,
                    checked = cfg.surroundSwap,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(surroundSwap = v) } }
                )
                OptionToggleRow(
                    title = strings.wavChannelSkip,
                    subtitle = strings.wavChannelSkipDesc,
                    checked = cfg.wavChannelSkip,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(wavChannelSkip = v) } }
                )
                HorizontalDivider(color = StudioBorder)
                OptionToggleRow(
                    title = strings.reportOnlyMode,
                    subtitle = strings.reportOnlyModeDesc,
                    checked = cfg.reportOnlyMode,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(reportOnlyMode = v) } }
                )
                OptionToggleRow(
                    title = strings.qualityGrading,
                    subtitle = strings.qualityGradingDesc,
                    checked = cfg.qualityGrading,
                    onCheckedChange = { v -> viewModel.updateRenderingConfig { it.copy(qualityGrading = v) } }
                )
            }
        }

        // 4. BATCH QUEUE PANEL (Port of Queue Grid in MainWindow.xaml)
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
                    Text(
                        text = "${strings.queueHeader} (${state.queue.size})",
                        style = MaterialTheme.typography.titleMedium
                    )
                    if (state.queue.any { it.status == JobStatus.COMPLETED }) {
                        OutlinedButton(onClick = { viewModel.clearCompletedQueueJobs() }) {
                            Text(strings.clearCompleted, style = MaterialTheme.typography.labelLarge)
                        }
                    }
                }

                Text(
                    text = strings.queueInfo,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                if (state.queue.isEmpty()) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .padding(16.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = strings.emptyQueueHint,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        state.queue.forEach { job ->
                            val isSelected = job.id == state.selectedQueueJobId
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(10.dp))
                                    .background(if (isSelected) StudioCard else StudioObsidian)
                                    .border(
                                        1.dp,
                                        if (isSelected) CavernCyan else StudioBorder,
                                        RoundedCornerShape(10.dp)
                                    )
                                    .clickable { viewModel.selectQueueJob(job.id) }
                                    .padding(10.dp),
                                verticalArrangement = Arrangement.spacedBy(4.dp)
                            ) {
                                Text(
                                    text = job.displayTitle(state.isPtBr),
                                    style = MaterialTheme.typography.bodySmall,
                                    fontWeight = FontWeight.Medium,
                                    color = when (job.status) {
                                        JobStatus.COMPLETED -> SpeakerActiveEmerald
                                        JobStatus.RUNNING -> CavernCyan
                                        else -> MaterialTheme.colorScheme.onSurface
                                    }
                                )
                                if (job.status == JobStatus.RUNNING || job.status == JobStatus.COMPLETED) {
                                    LinearProgressIndicator(
                                        progress = { job.progress },
                                        modifier = Modifier.fillMaxWidth(),
                                        color = SpeakerActiveEmerald
                                    )
                                }
                            }
                        }
                    }
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    OutlinedButton(
                        onClick = { viewModel.removeSelectedQueueJob() },
                        enabled = state.selectedQueueJobId != null && !state.isRendering,
                        modifier = Modifier
                            .weight(1f)
                            .height(48.dp)
                            .testTag("remove_queue_button")
                    ) {
                        Icon(Icons.Default.DeleteOutline, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.removeSelected)
                    }

                    Button(
                        onClick = { viewModel.processQueue() },
                        enabled = state.queue.isNotEmpty() && !state.isRendering,
                        colors = ButtonDefaults.buttonColors(containerColor = CavernCyan),
                        modifier = Modifier
                            .weight(1f)
                            .height(48.dp)
                            .testTag("process_queue_button")
                    ) {
                        Icon(Icons.Default.PlaylistPlay, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(strings.processQueue, fontWeight = FontWeight.Bold)
                    }
                }
            }
        }
    }
}

@Composable
fun OptionToggleRow(
    title: String,
    subtitle: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable { onCheckedChange(!checked) }
            .padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f).padding(end = 10.dp)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.SemiBold
            )
            Text(
                text = subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange
        )
    }
}
