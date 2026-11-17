package hu.sbence.cavernize.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import hu.sbence.cavernize.cavern.core.ChannelPrototype
import hu.sbence.cavernize.cavern.core.ReferenceChannel
import hu.sbence.cavernize.cavern.core.RenderTarget
import hu.sbence.cavernize.cavern.core.Vector3
import hu.sbence.cavernize.ui.theme.AtmosDynamicAmber
import hu.sbence.cavernize.ui.theme.CavernCyan
import hu.sbence.cavernize.ui.theme.JetBrainsMonoFamily
import hu.sbence.cavernize.ui.theme.SpeakerActiveEmerald
import hu.sbence.cavernize.ui.theme.SpeakerInactiveSlate
import hu.sbence.cavernize.ui.theme.StudioBorder
import hu.sbence.cavernize.ui.theme.StudioObsidian
import kotlin.math.ln

private val DisplaySpeakersOrder = listOf(
    ReferenceChannel.FrontLeft,
    ReferenceChannel.FrontCenter,
    ReferenceChannel.FrontRight,
    ReferenceChannel.WideLeft,
    ReferenceChannel.WideRight,
    ReferenceChannel.SideLeft,
    ReferenceChannel.SideRight,
    ReferenceChannel.RearLeft,
    ReferenceChannel.RearCenter,
    ReferenceChannel.RearRight,
    ReferenceChannel.TopFrontLeft,
    ReferenceChannel.TopFrontCenter,
    ReferenceChannel.TopFrontRight,
    ReferenceChannel.TopSideLeft,
    ReferenceChannel.GodsVoice,
    ReferenceChannel.TopSideRight,
    ReferenceChannel.TopRearLeft,
    ReferenceChannel.TopRearCenter,
    ReferenceChannel.TopRearRight
)

/**
 * Port of Cavern.WPF.Controls.StandardLayoutDisplay:
 * Renders the 3D speaker cube (outer ring = ground channels, inner elevated ring = height channels)
 * plus real-time moving 3D Atmos objects and VU meters.
 */
@Composable
fun StandardLayoutDisplayCanvas(
    renderTarget: RenderTarget,
    surroundSwap: Boolean,
    liveObjects: List<Vector3>,
    channelPeaks: Map<ReferenceChannel, Float>,
    isPtBr: Boolean,
    modifier: Modifier = Modifier
) {
    val textMeasurer = rememberTextMeasurer()
    val effectiveChannels = renderTarget.getEffectiveChannels(surroundSwap)
    val exportedSet = mutableSetOf<ReferenceChannel>()
    val downmixedSourceSet = mutableSetOf<ReferenceChannel>()

    for (i in effectiveChannels.indices) {
        if (renderTarget.isExported(i)) {
            exportedSet.add(effectiveChannels[i])
        } else {
            downmixedSourceSet.add(effectiveChannels[i])
        }
    }

    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(StudioObsidian)
            .border(1.dp, StudioBorder, RoundedCornerShape(14.dp))
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(210.dp)
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                val w = size.width
                val h = size.height
                val cx = w * 0.5f
                val cy = h * 0.5f
                val roomHalfW = minOf(w, h) * 0.42f
                val topHalfW = roomHalfW * 0.56f

                // Outer Ground Room Boundary
                drawRoundRect(
                    color = Color(0xFF1E2430),
                    topLeft = Offset(cx - roomHalfW, cy - roomHalfW),
                    size = Size(roomHalfW * 2f, roomHalfW * 2f),
                    cornerRadius = CornerRadius(16f, 16f),
                    style = Stroke(width = 2.5f)
                )

                // Inner Overhead Ceiling Boundary
                drawRoundRect(
                    color = Color(0xFF283244),
                    topLeft = Offset(cx - topHalfW, cy - topHalfW),
                    size = Size(topHalfW * 2f, topHalfW * 2f),
                    cornerRadius = CornerRadius(12f, 12f),
                    style = Stroke(
                        width = 2f,
                        pathEffect = PathEffect.dashPathEffect(floatArrayOf(10f, 8f), 0f)
                    )
                )

                // Perspective corner struts connecting ground and ceiling
                val corners = listOf(
                    Offset(-1f, -1f),
                    Offset(1f, -1f),
                    Offset(-1f, 1f),
                    Offset(1f, 1f)
                )
                for (c in corners) {
                    drawLine(
                        color = Color(0xFF1E293B),
                        start = Offset(cx + c.x * roomHalfW * 0.92f, cy + c.y * roomHalfW * 0.92f),
                        end = Offset(cx + c.x * topHalfW * 0.92f, cy + c.y * topHalfW * 0.92f),
                        strokeWidth = 1.5f
                    )
                }

                // Listener sweet-spot icon in center
                drawCircle(
                    color = Color(0xFF334155),
                    radius = 11f,
                    center = Offset(cx, cy)
                )

                // Draw all reference speakers
                for (ch in DisplaySpeakersOrder) {
                    val cubePos = ChannelPrototype.cubePosition(ch)
                    val isTop = ch.isHeightChannel
                    val scale = if (isTop) topHalfW else roomHalfW
                    val sx = cx + cubePos.x * scale
                    val sy = cy - cubePos.z * scale

                    val isActive = exportedSet.contains(ch)
                    val isMatrixed = downmixedSourceSet.contains(ch)
                    val isDynamicMode = renderTarget.isVirtualizer || renderTarget.isDriverDefault

                    val nodeColor = when {
                        isDynamicMode -> CavernCyan
                        isActive -> SpeakerActiveEmerald
                        isMatrixed -> AtmosDynamicAmber
                        else -> SpeakerInactiveSlate.copy(alpha = 0.45f)
                    }

                    val peak = channelPeaks[ch] ?: 0f
                    if (peak > 0.02f && (isActive || isDynamicMode)) {
                        drawCircle(
                            color = nodeColor.copy(alpha = 0.28f),
                            radius = 15f + peak * 18f,
                            center = Offset(sx, sy)
                        )
                    }

                    drawCircle(
                        color = nodeColor,
                        radius = if (isActive || isDynamicMode) 11.5f else 8f,
                        center = Offset(sx, sy)
                    )

                    val labelLayout = textMeasurer.measure(
                        text = ch.shortMark,
                        style = TextStyle(
                            fontFamily = JetBrainsMonoFamily,
                            fontSize = 9.sp,
                            fontWeight = FontWeight.Bold,
                            color = if (isActive || isDynamicMode || isMatrixed) Color.White else Color(0xFF64748B)
                        )
                    )
                    drawText(
                        textLayoutResult = labelLayout,
                        topLeft = Offset(
                            sx - labelLayout.size.width / 2f,
                            sy + (if (isTop) -22f else 12f)
                        )
                    )
                }

                // Draw live moving 3D Atmos objects
                for (objPos in liveObjects) {
                    val scale = roomHalfW * (1f - 0.35f * objPos.y.coerceIn(0f, 1f))
                    val ox = cx + objPos.x * scale
                    val oy = cy - objPos.z * scale
                    drawCircle(
                        color = AtmosDynamicAmber.copy(alpha = 0.3f),
                        radius = 14f,
                        center = Offset(ox, oy)
                    )
                    drawCircle(
                        color = AtmosDynamicAmber,
                        radius = 6.5f,
                        center = Offset(ox, oy)
                    )
                }
            }
        }

        // Legend Row
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            LegendBadge(
                color = SpeakerActiveEmerald,
                label = if (isPtBr) "Caixa Ativa" else "Active Speaker"
            )
            LegendBadge(
                color = AtmosDynamicAmber,
                label = if (isPtBr) "Matriz / Objeto 3D" else "Matrix / 3D Object"
            )
            LegendBadge(
                color = CavernCyan,
                label = if (isPtBr) "Virtual / Dinâmico" else "Virtual / Dynamic"
            )
        }
    }
}

@Composable
private fun LegendBadge(color: Color, label: String) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp)
    ) {
        Box(
            modifier = Modifier
                .size(9.dp)
                .clip(CircleShape)
                .background(color)
        )
        Text(
            text = label,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * Port of Cavern.WPF.Controls.GraphRendererControl:
 * Renders logarithmic frequency response (20 Hz .. 20 kHz) and optional phase curve (-180° .. +180°).
 */
@Composable
fun FrequencyResponseGraphCanvas(
    curvePoints: List<Triple<Float, Float, Float>>, // (freqHz, magDb, phaseDeg)
    showSpectrum: Boolean,
    showPhase: Boolean,
    modifier: Modifier = Modifier
) {
    val textMeasurer = rememberTextMeasurer()

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(185.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(StudioObsidian)
            .border(1.dp, StudioBorder, RoundedCornerShape(12.dp))
            .padding(10.dp)
    ) {
        Canvas(modifier = Modifier.fillMaxSize()) {
            val w = size.width
            val h = size.height
            val minLog = ln(20f)
            val maxLog = ln(20000f)

            // Horizontal dB grid lines (-24, -12, 0, +12, +24 dB)
            val dbLevels = listOf(-24f, -12f, 0f, 12f, 24f)
            for (db in dbLevels) {
                val y = h * (0.5f - (db / 60f))
                drawLine(
                    color = if (db == 0f) Color(0xFF334155) else Color(0xFF1E293B),
                    start = Offset(0f, y),
                    end = Offset(w, y),
                    strokeWidth = if (db == 0f) 2f else 1f
                )
                val lbl = textMeasurer.measure(
                    text = "${if (db > 0) "+" else ""}${db.toInt()} dB",
                    style = TextStyle(
                        fontFamily = JetBrainsMonoFamily,
                        fontSize = 9.sp,
                        color = Color(0xFF64748B)
                    )
                )
                drawText(lbl, topLeft = Offset(4f, (y - 14f).coerceAtLeast(0f)))
            }

            // Vertical log frequency markers (100 Hz, 1 kHz, 10 kHz)
            val freqMarkers = listOf(100f to "100Hz", 1000f to "1kHz", 10000f to "10kHz")
            for ((f, text) in freqMarkers) {
                val x = ((ln(f) - minLog) / (maxLog - minLog)) * w
                drawLine(
                    color = Color(0xFF1E293B),
                    start = Offset(x, 0f),
                    end = Offset(x, h),
                    strokeWidth = 1f
                )
                val lbl = textMeasurer.measure(
                    text = text,
                    style = TextStyle(
                        fontFamily = JetBrainsMonoFamily,
                        fontSize = 9.sp,
                        color = Color(0xFF64748B)
                    )
                )
                drawText(lbl, topLeft = Offset(x + 4f, h - 16f))
            }

            // Phase curve (-180°..+180°) in Amber
            if (showPhase && curvePoints.size > 1) {
                val phasePath = Path()
                curvePoints.forEachIndexed { idx, (freq, _, phase) ->
                    val x = ((ln(freq) - minLog) / (maxLog - minLog)) * w
                    val y = h * (0.5f - (phase / 360f))
                    if (idx == 0) phasePath.moveTo(x, y) else phasePath.lineTo(x, y)
                }
                drawPath(
                    path = phasePath,
                    color = AtmosDynamicAmber.copy(alpha = 0.85f),
                    style = Stroke(
                        width = 2.2f,
                        cap = StrokeCap.Round,
                        pathEffect = PathEffect.dashPathEffect(floatArrayOf(8f, 6f), 0f)
                    )
                )
            }

            // Magnitude curve (-30..+30 dB) in Cavern Cyan
            if (showSpectrum && curvePoints.size > 1) {
                val magPath = Path()
                val fillPath = Path()
                curvePoints.forEachIndexed { idx, (freq, db, _) ->
                    val x = ((ln(freq) - minLog) / (maxLog - minLog)) * w
                    val y = (h * (0.5f - (db / 60f))).coerceIn(2f, h - 2f)
                    if (idx == 0) {
                        magPath.moveTo(x, y)
                        fillPath.moveTo(x, h * 0.5f)
                        fillPath.lineTo(x, y)
                    } else {
                        magPath.lineTo(x, y)
                        fillPath.lineTo(x, y)
                    }
                }
                fillPath.lineTo(w, h * 0.5f)
                fillPath.close()

                drawPath(
                    path = fillPath,
                    brush = Brush.verticalGradient(
                        colors = listOf(CavernCyan.copy(alpha = 0.25f), Color.Transparent)
                    )
                )
                drawPath(
                    path = magPath,
                    color = CavernCyan,
                    style = Stroke(width = 3f, cap = StrokeCap.Round)
                )
            }
        }
    }
}
