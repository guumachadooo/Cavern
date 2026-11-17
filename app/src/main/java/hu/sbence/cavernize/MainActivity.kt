package hu.sbence.cavernize

import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Assessment
import androidx.compose.material.icons.filled.CallMerge
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.SpeakerGroup
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import hu.sbence.cavernize.cavern.core.RenderTarget
import hu.sbence.cavernize.cavern.core.RenderTargetCategory
import hu.sbence.cavernize.ui.CavernizeUiState
import hu.sbence.cavernize.ui.CavernizeViewModel
import hu.sbence.cavernize.ui.MainNavTab
import hu.sbence.cavernize.ui.screens.ConverterTabScreen
import hu.sbence.cavernize.ui.screens.DspAndUpmixScreen
import hu.sbence.cavernize.ui.screens.EnhancedAc3MergerScreen
import hu.sbence.cavernize.ui.screens.ReportsAndInfoScreen
import hu.sbence.cavernize.ui.theme.CavernCyan
import hu.sbence.cavernize.ui.theme.CavernizeTheme
import hu.sbence.cavernize.ui.theme.SpeakerActiveEmerald
import hu.sbence.cavernize.ui.theme.StudioBorder
import hu.sbence.cavernize.ui.theme.StudioCard
import hu.sbence.cavernize.ui.theme.StudioObsidian
import hu.sbence.cavernize.ui.theme.StudioSurface

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            CavernizeTheme {
                CavernizeAppRoot()
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CavernizeAppRoot(viewModel: CavernizeViewModel = viewModel()) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var pendingTextExport by remember { mutableStateOf<String?>(null) }

    // BackHandler for secondary tabs to return to CONVERTER home tab
    if (state.currentTab != MainNavTab.CONVERTER) {
        BackHandler {
            viewModel.selectTab(MainNavTab.CONVERTER)
        }
    }

    // Zero-permission Android Storage Access Framework (SAF) file opener
    val openDocumentLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            try {
                var displayName = "Loaded_Spatial_Stream.wav"
                context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                    if (cursor.moveToFirst()) {
                        val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                        if (idx >= 0) displayName = cursor.getString(idx) ?: displayName
                    }
                }
                val bytes = context.contentResolver.openInputStream(uri)?.use { stream ->
                    // Read up to 2 MB header/essence window so large movies never cause OOM
                    val buf = ByteArray(minOf(stream.available().coerceAtLeast(65536), 2 * 1024 * 1024))
                    val readCount = stream.read(buf)
                    if (readCount > 0) buf.copyOf(readCount) else ByteArray(0)
                } ?: ByteArray(0)
                viewModel.loadExternalFileBytes(displayName, bytes)
            } catch (_: Exception) {
            }
        }
    }

    // SAF binary export saver
    val saveDocumentLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.CreateDocument("*/*")
    ) { uri: Uri? ->
        if (uri != null) {
            try {
                context.contentResolver.openOutputStream(uri)?.use { out ->
                    val txt = pendingTextExport
                    if (txt != null) {
                        out.write(txt.toByteArray(Charsets.UTF_8))
                        pendingTextExport = null
                    } else {
                        state.lastExportedBytes?.let { out.write(it) }
                    }
                }
            } catch (_: Exception) {
            }
        }
    }

    Scaffold(
        modifier = Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.safeDrawing),
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = StudioSurface
                ),
                title = {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        Image(
                            painter = painterResource(id = R.drawable.cavern_logo),
                            contentDescription = stringResource(id = R.string.cd_logo),
                            modifier = Modifier.height(28.dp)
                        )
                        Column {
                            Text(
                                text = "Cavernize",
                                style = MaterialTheme.typography.titleMedium,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Text(
                                text = "Spatial Audio Engine • ${state.selectedRenderTarget.name}",
                                style = MaterialTheme.typography.labelMedium,
                                color = CavernCyan
                            )
                        }
                    }
                },
                actions = {
                    OutlinedButton(
                        onClick = { viewModel.toggleLanguage() },
                        modifier = Modifier
                            .padding(end = 8.dp)
                            .testTag("language_toggle_button")
                    ) {
                        Icon(
                            imageVector = Icons.Default.Language,
                            contentDescription = stringResource(id = R.string.cd_language_switch),
                            modifier = Modifier.size(16.dp)
                        )
                        Spacer(Modifier.width(4.dp))
                        Text(if (state.isPtBr) "PT-BR" else "EN")
                    }
                }
            )
        },
        bottomBar = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(StudioSurface)
                    .windowInsetsPadding(WindowInsets.navigationBars)
            ) {
                // Status Bar & Progress Bar (Port of StatusBar in MainWindow.xaml)
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(StudioObsidian)
                        .padding(horizontal = 14.dp, vertical = 6.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = state.statusMessage,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f)
                        )
                        if (state.isRendering) {
                            Text(
                                text = "${(state.renderProgress * 100).toInt()}%",
                                style = MaterialTheme.typography.labelMedium,
                                color = SpeakerActiveEmerald
                            )
                        }
                    }
                    LinearProgressIndicator(
                        progress = { state.renderProgress },
                        modifier = Modifier.fillMaxWidth().height(5.dp),
                        color = if (state.isRendering) CavernCyan else SpeakerActiveEmerald,
                        trackColor = StudioCard
                    )
                }

                HorizontalDivider(color = StudioBorder)

                NavigationBar(containerColor = StudioSurface) {
                    NavigationBarItem(
                        selected = state.currentTab == MainNavTab.CONVERTER,
                        onClick = { viewModel.selectTab(MainNavTab.CONVERTER) },
                        icon = { Icon(Icons.Default.SpeakerGroup, contentDescription = state.strings.tabConverter) },
                        label = { Text(state.strings.tabConverter) },
                        modifier = Modifier.testTag("nav_tab_converter")
                    )
                    NavigationBarItem(
                        selected = state.currentTab == MainNavTab.DSP_UPMIX,
                        onClick = { viewModel.selectTab(MainNavTab.DSP_UPMIX) },
                        icon = { Icon(Icons.Default.Tune, contentDescription = state.strings.tabDsp) },
                        label = { Text(state.strings.tabDsp) },
                        modifier = Modifier.testTag("nav_tab_dsp")
                    )
                    NavigationBarItem(
                        selected = state.currentTab == MainNavTab.EAC3_MERGER,
                        onClick = { viewModel.selectTab(MainNavTab.EAC3_MERGER) },
                        icon = { Icon(Icons.Default.CallMerge, contentDescription = state.strings.tabMerger) },
                        label = { Text(state.strings.tabMerger) },
                        modifier = Modifier.testTag("nav_tab_merger")
                    )
                    NavigationBarItem(
                        selected = state.currentTab == MainNavTab.REPORTS_INFO,
                        onClick = { viewModel.selectTab(MainNavTab.REPORTS_INFO) },
                        icon = { Icon(Icons.Default.Assessment, contentDescription = state.strings.tabReports) },
                        label = { Text(state.strings.tabReports) },
                        modifier = Modifier.testTag("nav_tab_reports")
                    )
                }
            }
        }
    ) { innerPadding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
        ) {
            when (state.currentTab) {
                MainNavTab.CONVERTER -> ConverterTabScreen(
                    state = state,
                    viewModel = viewModel,
                    onOpenSystemFilePicker = { openDocumentLauncher.launch(arrayOf("*/*")) },
                    onSaveExportedFile = {
                        val fileName = state.lastExportedFileName ?: "Cavern_Render.wav"
                        saveDocumentLauncher.launch(fileName)
                    }
                )
                MainNavTab.DSP_UPMIX -> DspAndUpmixScreen(
                    state = state,
                    viewModel = viewModel,
                    onExportEqualizerApoTxt = { apoTxt ->
                        pendingTextExport = apoTxt
                        saveDocumentLauncher.launch("Cavern_QuickEQ_Export.txt")
                    }
                )
                MainNavTab.EAC3_MERGER -> EnhancedAc3MergerScreen(
                    state = state,
                    viewModel = viewModel,
                    onSaveMergedFile = {
                        val fileName = state.lastExportedFileName ?: "Cavern_Merged.eac3"
                        saveDocumentLauncher.launch(fileName)
                    }
                )
                MainNavTab.REPORTS_INFO -> ReportsAndInfoScreen(state = state)
            }
        }
    }

    // Modal 1: RenderTargetSelector Dialog (Port of Windows/RenderTargetSelector.xaml)
    if (state.showRenderTargetDialog) {
        RenderTargetSelectorDialog(
            state = state,
            onSelectTarget = { viewModel.selectRenderTarget(it) },
            onDismiss = { viewModel.setShowRenderTargetDialog(false) }
        )
    }

    // Modal 2: Display Wiring Dialog (Port of DisplayWiring in MainWindow.Popups.cs)
    if (state.showWiringDialog) {
        WiringDiagramDialog(
            state = state,
            onDismiss = { viewModel.setShowWiringDialog(false) }
        )
    }

    // Modal 3: Built-in 3D Spatial Demo Generator Dialog
    if (state.showDemoGeneratorDialog) {
        DemoGeneratorDialog(
            state = state,
            onSelectPreset = { viewModel.loadDemoPreset(it) },
            onDismiss = { viewModel.setShowDemoGeneratorDialog(false) }
        )
    }

    // Modal 4: Codec Metadata Inspector Dialog
    if (state.showMetadataModal) {
        CodecMetadataDialog(
            state = state,
            onDismiss = { viewModel.setShowMetadataModal(false) }
        )
    }
}

@Composable
private fun RenderTargetSelectorDialog(
    state: CavernizeUiState,
    onSelectTarget: (RenderTarget) -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(state.strings.selectRenderTargetTitle, style = MaterialTheme.typography.titleMedium)
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 420.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                RenderTargetCategory.entries.forEach { category ->
                    Text(
                        text = if (state.isPtBr) category.labelPt else category.labelEn,
                        style = MaterialTheme.typography.labelLarge,
                        color = CavernCyan,
                        modifier = Modifier.padding(top = 6.dp)
                    )
                    RenderTarget.Targets.filter { it.category == category }.forEach { target ->
                        val isSelected = target.name == state.selectedRenderTarget.name
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .background(if (isSelected) StudioCard else StudioObsidian)
                                .border(
                                    1.dp,
                                    if (isSelected) SpeakerActiveEmerald else StudioBorder,
                                    RoundedCornerShape(10.dp)
                                )
                                .clickable { onSelectTarget(target) }
                                .padding(horizontal = 12.dp, vertical = 10.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = target.name,
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontWeight = FontWeight.Bold,
                                    color = if (isSelected) SpeakerActiveEmerald else MaterialTheme.colorScheme.onSurface
                                )
                                Text(
                                    text = target.channels.joinToString(", ") { it.shortMark },
                                    style = MaterialTheme.typography.labelMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            Text(
                                text = "${target.outputChannels} ch",
                                style = MaterialTheme.typography.labelLarge,
                                color = CavernCyan
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text(if (state.isPtBr) "Fechar" else "Close")
            }
        }
    )
}

@Composable
private fun WiringDiagramDialog(
    state: CavernizeUiState,
    onDismiss: () -> Unit
) {
    val wiringPairs = state.selectedRenderTarget.getWiringMap(
        surroundSwap = state.renderingConfig.surroundSwap,
        isPtBr = state.isPtBr
    )
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text("${state.strings.wiringTitle} (${state.selectedRenderTarget.name})", style = MaterialTheme.typography.titleMedium)
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Text(
                    text = state.strings.wiringDescription,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                wiringPairs.forEach { (jack, channelsDesc) ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .padding(10.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Text(jack, style = MaterialTheme.typography.labelLarge, color = CavernCyan)
                        Text(channelsDesc, style = MaterialTheme.typography.bodySmall, color = SpeakerActiveEmerald)
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) {
                Text("OK")
            }
        }
    )
}

@Composable
private fun DemoGeneratorDialog(
    state: CavernizeUiState,
    onSelectPreset: (String) -> Unit,
    onDismiss: () -> Unit
) {
    val presets = listOf(
        "Cinema_Atmos_Showcase_7.1.4.mkv" to (if (state.isPtBr) "Container Matroska MKV (E-AC-3 JOC 16 obj + PCM 7.1)" else "Matroska MKV Container (E-AC-3 JOC 16 obj + 7.1 PCM)"),
        "Dolby_Atmos_Horizon_JOC.eac3" to (if (state.isPtBr) "Bitstream Enhanced AC-3 com Joint Object Coding (Atmos)" else "Enhanced AC-3 Bitstream with Joint Object Coding (Atmos)"),
        "TrueHD_Lossless_Master.mlp" to (if (state.isPtBr) "Dolby TrueHD / Meridian Lossless Packing (4ª Substream Atmos)" else "Dolby TrueHD / Meridian Lossless Packing (4th Substream Atmos)"),
        "Studio_ADM_Master_24bit.wav" to (if (state.isPtBr) "ADM Broadcast Wave Format (ITU-R BS.2076 axml + chna)" else "ADM Broadcast Wave Format (ITU-R BS.2076 axml + chna)"),
        "Cavern_Spatial_Reference.laf" to (if (state.isPtBr) "Limitless Audio Format (.laf Nativo do Cavern)" else "Limitless Audio Format (.laf Native Cavern Spatial)"),
        "Apple_Spatial_Master.caf" to (if (state.isPtBr) "Apple Core Audio Format (.caf com DBMD Atmos)" else "Apple Core Audio Format (.caf with DBMD Atmos)"),
        "ISO_Movie_Atmos_Stream.mp4" to (if (state.isPtBr) "Container MP4 ISO Base Media (ftyp/moov/ec-3)" else "ISO Base Media MP4 Container (ftyp/moov/ec-3)"),
        "Cinema_DCP_Immersive.mxf" to (if (state.isPtBr) "SMPTE MXF Digital Cinema Package (KLV 7.1.4 + Objetos)" else "SMPTE MXF Digital Cinema Package (KLV 7.1.4 + Objects)"),
        "Studio_Production_Master.atmos" to (if (state.isPtBr) "Dolby Atmos Master Format (.atmos YAML/XML)" else "Dolby Atmos Master Format (.atmos YAML/XML)")
    )

    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(state.strings.demoMenuTitle, style = MaterialTheme.typography.titleMedium)
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 400.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                presets.forEach { (fileName, desc) ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .clickable { onSelectPreset(fileName) }
                            .padding(10.dp),
                        verticalArrangement = Arrangement.spacedBy(3.dp)
                    ) {
                        Text(fileName, style = MaterialTheme.typography.labelLarge, color = SpeakerActiveEmerald)
                        Text(desc, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text(if (state.isPtBr) "Cancelar" else "Cancel")
            }
        }
    )
}

@Composable
private fun CodecMetadataDialog(
    state: CavernizeUiState,
    onDismiss: () -> Unit
) {
    val meta = state.selectedTrack?.metadata
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(state.strings.codecMetadataTitle, style = MaterialTheme.typography.titleMedium)
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Default.Close, contentDescription = null)
                }
            }
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 400.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                meta?.headers?.forEach { section ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(StudioObsidian)
                            .border(1.dp, StudioBorder, RoundedCornerShape(10.dp))
                            .padding(10.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Text(section.title, style = MaterialTheme.typography.labelLarge, color = CavernCyan)
                        HorizontalDivider(color = StudioBorder)
                        section.fields.forEach { field ->
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                Text(field.name, style = MaterialTheme.typography.labelMedium, color = SpeakerActiveEmerald)
                                Text(field.value, style = MaterialTheme.typography.labelMedium)
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) {
                Text("OK")
            }
        }
    )
}
