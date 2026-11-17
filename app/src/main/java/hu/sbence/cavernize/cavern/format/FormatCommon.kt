package hu.sbence.cavernize.cavern.format

import hu.sbence.cavernize.cavern.core.ReferenceChannel
import hu.sbence.cavernize.cavern.core.RenderTarget
import hu.sbence.cavernize.cavern.core.SpatialSource

enum class BitDepth(val bits: Int, val label: String) {
    Int8(8, "8-bit PCM"),
    Int16(16, "16-bit PCM"),
    Int24(24, "24-bit PCM"),
    Float32(32, "32-bit Float IEEE")
}

/**
 * All containers from Cavern.Format.Common.Container.
 */
enum class Container(val displayName: String, val defaultExtension: String) {
    NotContainer("Standalone Audio Stream", ".wav"),
    Matroska("Matroska / WebM (MKV/MKA)", ".mkv"),
    MP4("ISO Base Media (MP4/MOV/M4A)", ".mp4"),
    MXF("Material Exchange Format (SMPTE MXF)", ".mxf"),
    ElementaryStream("Elementary Bitstream", ".eac3")
}

/**
 * All codecs from Cavern.Format.Common.Codec.
 */
enum class Codec(
    val displayName: String,
    val isEnvironmental: Boolean = false,
    val defaultExtension: String = ".wav"
) {
    PCM_LE("PCM (Integer Little-Endian)", false, ".wav"),
    PCM_Float("PCM (Floating Point)", false, ".wav"),
    AC3("Dolby Digital (AC-3)", false, ".ac3"),
    EnhancedAC3("Dolby Digital Plus (Enhanced AC-3 / JOC Atmos)", false, ".eac3"),
    TrueHD("Dolby TrueHD / Meridian Lossless Packing (MLP)", false, ".mlp"),
    DTS("DTS Coherent Acoustics / DTS:X", false, ".dts"),
    Opus("Opus Multichannel", false, ".opus"),
    FLAC("FLAC Lossless Multichannel", false, ".flac"),
    LimitlessAudio("Limitless Audio Format (LAF)", true, ".laf"),
    CoreAudio("Apple Core Audio Format (CAF)", false, ".caf"),
    ADM_BWF("ADM Broadcast Wave Format (Compact)", true, ".wav"),
    ADM_BWF_Atmos("ADM Broadcast Wave Format (Dolby Atmos)", true, ".wav"),
    DAMF("Dolby Atmos Master Format (.atmos)", true, ".atmos")
}

/**
 * Export format matching Cavernize.Logic.Models.ExportFormat.
 */
data class ExportFormat(
    val codec: Codec,
    val ffName: String,
    val maxChannels: Int,
    val descriptionPt: String,
    val descriptionEn: String
) {
    fun getDescription(isPtBr: Boolean): String = if (isPtBr) descriptionPt else descriptionEn

    companion object {
        val AllFormats: List<ExportFormat> = listOf(
            ExportFormat(
                Codec.AC3, "ac3", 6,
                "AC-3 (6 canais máx., compatível com S/PDIF óptico)",
                "AC-3 (mediocre, max 6 ch, supports S/PDIF)"
            ),
            ExportFormat(
                Codec.EnhancedAC3, "eac3", 16,
                "Enhanced AC-3 (até 16 canais, suporta HDMI ARC / eARC)",
                "Enhanced AC-3 (up to 16 ch, supports HDMI ARC)"
            ),
            ExportFormat(
                Codec.Opus, "libopus", 64,
                "Opus Multicanal (transparente, arquivo compacto)",
                "Opus (transparent quality, small size)"
            ),
            ExportFormat(
                Codec.FLAC, "flac", 8,
                "FLAC Lossless (sem perdas, até 8 canais)",
                "FLAC (lossless compression, up to 8 ch)"
            ),
            ExportFormat(
                Codec.PCM_LE, "pcm_s16le", 64,
                "PCM RIFF WAVE Inteiro (sem perdas, 16/24-bit, até 64 canais)",
                "PCM, integer RIFF WAVE (lossless, 16/24-bit, up to 64 ch)"
            ),
            ExportFormat(
                Codec.PCM_Float, "pcm_f32le", 64,
                "PCM RIFF WAVE Float 32-bit (precisão máxima IEEE 754)",
                "PCM, 32-bit float (maximum headroom, largest size)"
            ),
            ExportFormat(
                Codec.ADM_BWF, "", 128,
                "ADM Broadcast Wave Format (compacto com objetos 3D)",
                "ADM Broadcast Wave Format (compact object-based)"
            ),
            ExportFormat(
                Codec.ADM_BWF_Atmos, "", 128,
                "ADM Broadcast Wave Format (Mastezização Dolby Atmos)",
                "ADM Broadcast Wave Format (Dolby Atmos master)"
            ),
            ExportFormat(
                Codec.DAMF, "", 128,
                "Dolby Atmos Master Format (.atmos + .wav + .aet)",
                "Dolby Atmos Master Format (.atmos set)"
            ),
            ExportFormat(
                Codec.LimitlessAudio, "", Int.MAX_VALUE,
                "Limitless Audio Format (.laf espacial nativo do Cavern)",
                "Limitless Audio Format (.laf native Cavern spatial)"
            )
        )
    }
}

/**
 * Port of Cavern.Format.Common.ReadableMetadata & ReadableMetadataHeader.
 */
data class MetadataField(
    val name: String,
    val value: String,
    val description: String = ""
)

data class MetadataSection(
    val title: String,
    val fields: List<MetadataField>
)

data class ReadableMetadata(
    val headers: List<MetadataSection>
)

/**
 * Port of Cavernize.Logic.Models.CavernizeTrack.
 */
data class CavernizeTrack(
    val id: Int,
    val title: String,
    val languageCode: String,
    val codec: Codec,
    val container: Container,
    val sampleRate: Int,
    val lengthSamples: Long,
    val bitDepth: BitDepth,
    val bedChannels: List<ReferenceChannel>,
    val dynamicObjectCount: Int,
    val hasJointObjectCoding: Boolean,
    val supported: Boolean = true,
    val sources: List<SpatialSource>,
    val metadata: ReadableMetadata,
    val pcmGenerator: (Int, Int) -> Array<FloatArray>
) {
    val totalChannels: Int
        get() = bedChannels.size + dynamicObjectCount

    val durationSeconds: Double
        get() = if (sampleRate > 0) lengthSamples.toDouble() / sampleRate else 0.0

    fun formatDuration(): String {
        val totalSec = durationSeconds.toLong()
        val mins = totalSec / 60
        val secs = totalSec % 60
        val ms = ((durationSeconds - totalSec) * 100).toInt()
        return String.format("%02d:%02d.%02d", mins, secs, ms)
    }

    fun getDetailedInfo(isPtBr: Boolean): String {
        val sb = StringBuilder()
        if (hasJointObjectCoding) {
            sb.appendLine(
                if (isPtBr) "Enhanced AC-3 com Joint Object Coding (Dolby Atmos)"
                else "Enhanced AC-3 with Joint Object Coding (Dolby Atmos)"
            )
        } else if (dynamicObjectCount > 0) {
            sb.appendLine(if (isPtBr) "Faixa de áudio baseada em objetos 3D" else "Object-based 3D audio track")
        } else {
            sb.appendLine(if (isPtBr) "Faixa de áudio baseada em canais" else "Channel-based audio track")
        }

        val bedMarks = bedChannels.joinToString(", ") { it.shortMark }
        if (dynamicObjectCount > 0) {
            sb.append(if (isPtBr) "Canais de cama: " else "Bed channels: ")
                .append(bedChannels.size)
                .append(" (").append(bedMarks).appendLine(")")
            sb.append(if (isPtBr) "Objetos dinâmicos: " else "Dynamic objects: ")
                .appendLine(dynamicObjectCount)
        } else {
            sb.append(if (isPtBr) "Canais de origem: " else "Source channels: ")
                .append(bedChannels.size)
                .append(" (").append(bedMarks).appendLine(")")
        }

        sb.append(String.format("%d Hz • %s • Duração: %s", sampleRate, bitDepth.label, formatDuration()))
        return sb.toString()
    }
}

data class LoadedAudioFile(
    val fileName: String,
    val fileSize: Long,
    val container: Container,
    val tracks: List<CavernizeTrack>
)

enum class JobStatus {
    QUEUED,
    RUNNING,
    COMPLETED,
    FAILED
}

data class QueuedJob(
    val id: Long,
    val fileName: String,
    val track: CavernizeTrack,
    val renderTarget: RenderTarget,
    val exportFormat: ExportFormat,
    val status: JobStatus = JobStatus.QUEUED,
    val progress: Float = 0f,
    val outputSummary: String = ""
) {
    fun displayTitle(isPtBr: Boolean): String {
        val statusText = when (status) {
            JobStatus.QUEUED -> if (isPtBr) "Aguardando" else "Queued"
            JobStatus.RUNNING -> String.format("%.0f%%", progress * 100f)
            JobStatus.COMPLETED -> if (isPtBr) "Concluído" else "Done"
            JobStatus.FAILED -> if (isPtBr) "Erro" else "Failed"
        }
        return "$fileName [${track.codec.displayName}] → ${renderTarget.name} (${exportFormat.codec.name}) • $statusText"
    }
}
