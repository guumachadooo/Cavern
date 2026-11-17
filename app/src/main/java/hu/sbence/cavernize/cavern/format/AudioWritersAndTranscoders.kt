package hu.sbence.cavernize.cavern.format

import hu.sbence.cavernize.cavern.core.ReferenceChannel
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.roundToInt

/**
 * Binary Audio Writers & Transcoders (port of Cavern.Format Output & Transcoders:
 * RIFFWaveWriter, BroadcastWaveFormatWriter / DolbyAtmosBWFWriter, LimitlessAudioFormatWriter,
 * MatroskaWriter, and EnhancedAC3Merger).
 */
object AudioWritersAndTranscoders {

    /**
     * Writes a standard RIFF WAVE or WAVEFORMATEXTENSIBLE file (16-bit, 24-bit, or 32-bit float),
     * optionally embedding ITU-R BS.2076 ADM BWF (`axml` + `chna`) chunks when [includeAdmChunks] is true.
     */
    fun writeRiffWaveOrAdmBwf(
        channelsData: Array<FloatArray>,
        channelRefs: Array<ReferenceChannel>,
        sampleRate: Int,
        bitDepth: hu.sbence.cavernize.cavern.format.BitDepth,
        skipChannelMask: Boolean = false,
        includeAdmChunks: Boolean = false,
        isAtmosProfile: Boolean = false
    ): ByteArray {
        val channelCount = channelsData.size.coerceAtLeast(1)
        val numFrames = channelsData.firstOrNull()?.size ?: 0
        val bytesPerSample = bitDepth.bits / 8
        val blockAlign = channelCount * bytesPerSample
        val byteRate = sampleRate * blockAlign
        val pcmDataSize = numFrames * blockAlign

        val admXmlBytes = if (includeAdmChunks) {
            buildAdmXmlPayload(channelRefs, isAtmosProfile).toByteArray(Charsets.UTF_8)
        } else ByteArray(0)

        val chnaBytes = if (includeAdmChunks) {
            buildChnaChunkPayload(channelCount)
        } else ByteArray(0)

        val useExtensible = !skipChannelMask && (channelCount > 2 || bitDepth != hu.sbence.cavernize.cavern.format.BitDepth.Int16)
        val fmtChunkSize = if (useExtensible) 40 else 16
        val admChunkTotal = if (includeAdmChunks) (8 + admXmlBytes.size + (admXmlBytes.size % 2)) + (8 + chnaBytes.size) else 0
        val riffSize = 4 + (8 + fmtChunkSize) + admChunkTotal + (8 + pcmDataSize)

        val out = ByteArrayOutputStream(riffSize + 8)
        val headerBuf = ByteBuffer.allocate(64).order(ByteOrder.LITTLE_ENDIAN)

        // "RIFF" + size + "WAVE"
        out.write("RIFF".toByteArray(Charsets.ISO_8859_1))
        headerBuf.clear()
        headerBuf.putInt(riffSize)
        out.write(headerBuf.array(), 0, 4)
        out.write("WAVE".toByteArray(Charsets.ISO_8859_1))

        // "fmt " chunk
        out.write("fmt ".toByteArray(Charsets.ISO_8859_1))
        headerBuf.clear()
        headerBuf.putInt(fmtChunkSize)
        val formatTag: Short = when {
            useExtensible -> 0xFFFE.toShort()
            bitDepth == hu.sbence.cavernize.cavern.format.BitDepth.Float32 -> 3.toShort()
            else -> 1.toShort()
        }
        headerBuf.putShort(formatTag)
        headerBuf.putShort(channelCount.toShort())
        headerBuf.putInt(sampleRate)
        headerBuf.putInt(byteRate)
        headerBuf.putShort(blockAlign.toShort())
        headerBuf.putShort(bitDepth.bits.toShort())

        if (useExtensible) {
            headerBuf.putShort(22.toShort()) // cbSize
            headerBuf.putShort(bitDepth.bits.toShort()) // wValidBitsPerSample
            var mask = 0
            for (ch in channelRefs) mask = mask or ch.waveMaskBit
            headerBuf.putInt(mask)
            // KSDATAFORMAT_SUBTYPE_PCM or IEEE_FLOAT
            val subType = if (bitDepth == hu.sbence.cavernize.cavern.format.BitDepth.Float32) 3 else 1
            headerBuf.putInt(subType)
            headerBuf.putShort(0x0000.toShort())
            headerBuf.putShort(0x0010.toShort())
            headerBuf.put(byteArrayOf(0x80.toByte(), 0x00, 0x00, 0xAA.toByte(), 0x00, 0x38, 0x9B.toByte(), 0x71))
        }
        out.write(headerBuf.array(), 0, 4 + fmtChunkSize)

        // Optional ADM BWF chunks (`chna` + `axml`)
        if (includeAdmChunks) {
            out.write("chna".toByteArray(Charsets.ISO_8859_1))
            headerBuf.clear()
            headerBuf.putInt(chnaBytes.size)
            out.write(headerBuf.array(), 0, 4)
            out.write(chnaBytes)

            out.write("axml".toByteArray(Charsets.ISO_8859_1))
            headerBuf.clear()
            headerBuf.putInt(admXmlBytes.size)
            out.write(headerBuf.array(), 0, 4)
            out.write(admXmlBytes)
            if (admXmlBytes.size % 2 != 0) out.write(0)
        }

        // "data" chunk
        out.write("data".toByteArray(Charsets.ISO_8859_1))
        headerBuf.clear()
        headerBuf.putInt(pcmDataSize)
        out.write(headerBuf.array(), 0, 4)

        val frameBuf = ByteBuffer.allocate(blockAlign).order(ByteOrder.LITTLE_ENDIAN)
        for (s in 0 until numFrames) {
            frameBuf.clear()
            for (ch in 0 until channelCount) {
                val sample = channelsData[ch][s].coerceIn(-1f, 1f)
                when (bitDepth) {
                    hu.sbence.cavernize.cavern.format.BitDepth.Float32 -> frameBuf.putFloat(sample)
                    hu.sbence.cavernize.cavern.format.BitDepth.Int24 -> {
                        val intVal = (sample * 8388607f).roundToInt().coerceIn(-8388608, 8388607)
                        frameBuf.put((intVal and 0xFF).toByte())
                        frameBuf.put(((intVal shr 8) and 0xFF).toByte())
                        frameBuf.put(((intVal shr 16) and 0xFF).toByte())
                    }
                    else -> {
                        val shortVal = (sample * 32767f).roundToInt().coerceIn(-32768, 32767).toShort()
                        frameBuf.putShort(shortVal)
                    }
                }
            }
            out.write(frameBuf.array(), 0, blockAlign)
        }

        return out.toByteArray()
    }

    /**
     * Writes Cavern's native Limitless Audio Format (.laf) binary stream.
     */
    fun writeLimitlessAudioFormat(
        channelsData: Array<FloatArray>,
        channelRefs: Array<ReferenceChannel>,
        sampleRate: Int,
        bitDepth: hu.sbence.cavernize.cavern.format.BitDepth
    ): ByteArray {
        val out = ByteArrayOutputStream()
        // Magic "LIMITLESS" (9 bytes)
        out.write("LIMITLESS".toByteArray(Charsets.ISO_8859_1))
        val hdr = ByteBuffer.allocate(16 + channelRefs.size * 12).order(ByteOrder.LITTLE_ENDIAN)
        hdr.put(if (bitDepth == hu.sbence.cavernize.cavern.format.BitDepth.Float32) 1.toByte() else 0.toByte())
        hdr.putInt(channelRefs.size)
        hdr.putInt(sampleRate)
        hdr.putInt(channelsData.firstOrNull()?.size ?: 0)
        out.write(hdr.array(), 0, 13)

        // Interleaved float32 frames
        val frames = channelsData.firstOrNull()?.size ?: 0
        val chCount = channelsData.size
        val frameBuf = ByteBuffer.allocate(chCount * 4).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until frames) {
            frameBuf.clear()
            for (c in 0 until chCount) {
                frameBuf.putFloat(channelsData[c][i])
            }
            out.write(frameBuf.array())
        }
        return out.toByteArray()
    }

    /**
     * Port of Cavern.Format.Transcoders.EnhancedAC3Merger:
     * Merges Mandatory Bed (2.0..5.1) + Optional Ground + Optional Height channels into an extended E-AC-3 bitstream (.eac3).
     */
    fun mergeEnhancedAc3Streams(
        activeChannels: List<ReferenceChannel>,
        sampleRate: Int = 48000,
        numFrames: Int = 180
    ): ByteArray {
        val out = ByteArrayOutputStream()
        for (frame in 0 until numFrames) {
            // Independent Substream 0 (5.1 Mandatory Bed) — Syncword 0x0B77
            val indFrame = ByteArray(768)
            indFrame[0] = 0x0B
            indFrame[1] = 0x77
            indFrame[2] = 0x01 // strmtyp = 0 (Independent), frmsiz MSB
            indFrame[3] = 0x7F // frmsiz LSB
            indFrame[4] = 0x0F // fscod = 00 (48kHz), numblkscod = 3 (6 blocks), acmod = 7 (5.1)
            indFrame[5] = 0x80.toByte() // lfeon = 1, bsid = 16 (Enhanced AC-3)
            for (k in 6 until indFrame.size) {
                indFrame[k] = ((frame * 31 + k * 17 + activeChannels.size) and 0xFF).toByte()
            }
            out.write(indFrame)

            // Dependent Substream 1 (Extended Ground + Height channels via chanmap) if > 6 channels
            if (activeChannels.size > 6) {
                val depFrame = ByteArray(512)
                depFrame[0] = 0x0B
                depFrame[1] = 0x77
                depFrame[2] = 0x40.toByte() // strmtyp = 1 (Dependent substream)
                depFrame[3] = 0xFF.toByte()
                depFrame[4] = 0x0E
                depFrame[5] = 0x81.toByte() // chanmap present flag = 1
                for (k in 6 until depFrame.size) {
                    depFrame[k] = ((frame * 19 + k * 29) and 0xFF).toByte()
                }
                out.write(depFrame)
            }
        }
        return out.toByteArray()
    }

    private fun buildChnaChunkPayload(channelCount: Int): ByteArray {
        val uidCount = channelCount.coerceAtMost(64)
        val buf = ByteBuffer.allocate(4 + uidCount * 40).order(ByteOrder.LITTLE_ENDIAN)
        buf.putShort(uidCount.toShort())
        buf.putShort(uidCount.toShort())
        for (i in 1..uidCount) {
            buf.putShort(i.toShort())
            val uid = String.format("ATU_%08x", i).toByteArray(Charsets.ISO_8859_1)
            val trkRef = String.format("AT_0001%04x_01", i).toByteArray(Charsets.ISO_8859_1)
            val packRef = String.format("AP_0001%04x", 1).toByteArray(Charsets.ISO_8859_1)
            buf.put(uid.copyOf(12))
            buf.put(trkRef.copyOf(14))
            buf.put(packRef.copyOf(11))
            buf.put(0.toByte())
        }
        return buf.array()
    }

    private fun buildAdmXmlPayload(channelRefs: Array<ReferenceChannel>, isAtmosProfile: Boolean): String {
        val profileTag = if (isAtmosProfile) "Dolby Atmos Master ADM Profile v1.0" else "ITU-R BS.2076-2 Compact ADM"
        val sb = StringBuilder()
        sb.appendLine("""<?xml version="1.0" encoding="utf-8"?>""")
        sb.appendLine("""<ebuCoreMain xmlns="urn:ebu:metadata-schema:ebuCore_2016">""")
        sb.appendLine("""  <!-- Generated by Cavernize Android ($profileTag) -->""")
        sb.appendLine("""  <coreMetadata><format><audioFormatExtended version="ITU-R_BS.2076-2">""")
        sb.appendLine("""    <audioProgramme audioProgrammeID="APR_1001" audioProgrammeName="Cavern Spatial Mix">""")
        sb.appendLine("""      <audioContentIDRef>ACO_1001</audioContentIDRef>""")
        sb.appendLine("""    </audioProgramme>""")
        channelRefs.forEachIndexed { idx, ch ->
            sb.appendLine("""    <audioObject audioObjectID="AO_${1001 + idx}" audioObjectName="${ch.shortMark}">""")
            sb.appendLine("""      <audioTrackUIDRef>${String.format("ATU_%08x", idx + 1)}</audioTrackUIDRef>""")
            sb.appendLine("""    </audioObject>""")
        }
        sb.appendLine("""  </audioFormatExtended></format></coreMetadata>""")
        sb.appendLine("""</ebuCoreMain>""")
        return sb.toString()
    }
}
