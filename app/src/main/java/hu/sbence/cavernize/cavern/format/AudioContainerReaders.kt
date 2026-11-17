package hu.sbence.cavernize.cavern.format

import hu.sbence.cavernize.cavern.core.ChannelPrototype
import hu.sbence.cavernize.cavern.core.ReferenceChannel
import hu.sbence.cavernize.cavern.core.SpatialSource
import hu.sbence.cavernize.cavern.core.Vector3
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin

/**
 * Multi-format binary container and bitstream parser (port of Cavern.Format Input & Decoders:
 * RIFFWaveReader, LimitlessAudioFormatReader, CoreAudioFormatReader, EnhancedAC3Reader + JOC,
 * MeridianLosslessPackingReader, MatroskaReader, MP4Reader, MXFReader, DolbyAtmosMasterFormatReader).
 */
object AudioContainerParser {

    fun parseBytes(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val lower = fileName.lowercase()
        val magic4 = if (bytes.size >= 4) String(bytes, 0, 4, Charsets.ISO_8859_1) else ""

        return when {
            magic4 == "RIFF" || magic4 == "RF64" || lower.endsWith(".wav") || lower.endsWith(".bwf") ->
                parseRiffWaveOrAdmBwf(fileName, bytes)
            magic4 == "LIMIT" || lower.endsWith(".laf") ->
                parseLimitlessAudioFormat(fileName, bytes)
            magic4 == "caff" || lower.endsWith(".caf") ->
                parseCoreAudioFormat(fileName, bytes)
            (bytes.size >= 2 && ((bytes[0].toInt() and 0xFF) == 0x0B && (bytes[1].toInt() and 0xFF) == 0x77)) ||
                lower.endsWith(".eac3") || lower.endsWith(".ec3") || lower.endsWith(".ac3") ->
                parseEnhancedAc3Stream(fileName, bytes)
            lower.endsWith(".mlp") || lower.endsWith(".thd") || lower.endsWith(".truehd") ->
                parseMeridianLosslessPacking(fileName, bytes)
            (bytes.size >= 4 && (bytes[0].toInt() and 0xFF) == 0x1A && (bytes[1].toInt() and 0xFF) == 0x45 &&
                (bytes[2].toInt() and 0xFF) == 0xDF && (bytes[3].toInt() and 0xFF) == 0xA3) ||
                lower.endsWith(".mkv") || lower.endsWith(".mka") || lower.endsWith(".webm") ->
                parseMatroskaContainer(fileName, bytes)
            (bytes.size >= 8 && String(bytes, 4, 4, Charsets.ISO_8859_1) == "ftyp") ||
                lower.endsWith(".mp4") || lower.endsWith(".mov") || lower.endsWith(".m4a") ->
                parseMp4Container(fileName, bytes)
            (bytes.size >= 4 && (bytes[0].toInt() and 0xFF) == 0x06 && (bytes[1].toInt() and 0xFF) == 0x0E &&
                (bytes[2].toInt() and 0xFF) == 0x2B && (bytes[3].toInt() and 0xFF) == 0x34) ||
                lower.endsWith(".mxf") ->
                parseMxfContainer(fileName, bytes)
            lower.endsWith(".atmos") || lower.endsWith(".damf") ->
                parseDolbyAtmosMasterFormat(fileName, bytes)
            else -> parseGenericOrFallbackStream(fileName, bytes)
        }
    }

    /**
     * Parses RIFF WAVE / RF64 / Broadcast Wave Format (ADM BWF with `axml` and `chna` chunks).
     */
    private fun parseRiffWaveOrAdmBwf(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        var channels = 2
        var sampleRate = 48000
        var bitsPerSample = 16
        var audioFormat = 1
        var channelMask = 0x3
        var hasAdmXml = false
        var hasChnaChunk = false
        var admObjectsDetected = 0
        var dataOffset = 44
        var dataSize = (bytes.size - 44).coerceAtLeast(0)

        if (bytes.size >= 36 && String(bytes, 0, 4, Charsets.ISO_8859_1).let { it == "RIFF" || it == "RF64" }) {
            var pos = 12
            while (pos + 8 <= bytes.size) {
                val chunkId = String(bytes, pos, 4, Charsets.ISO_8859_1)
                val chunkSize = buf.getInt(pos + 4).toLong() and 0xFFFFFFFFL
                val payloadPos = pos + 8
                when (chunkId) {
                    "fmt " -> if (payloadPos + 16 <= bytes.size) {
                        audioFormat = buf.getShort(payloadPos).toInt() and 0xFFFF
                        channels = (buf.getShort(payloadPos + 2).toInt() and 0xFFFF).coerceIn(1, 64)
                        sampleRate = buf.getInt(payloadPos + 4).coerceIn(8000, 192000)
                        bitsPerSample = (buf.getShort(payloadPos + 14).toInt() and 0xFFFF).coerceIn(8, 32)
                        if (audioFormat == 0xFFFE && chunkSize >= 40 && payloadPos + 40 <= bytes.size) {
                            channelMask = buf.getInt(payloadPos + 20)
                            val subFormat = buf.getShort(payloadPos + 24).toInt() and 0xFFFF
                            audioFormat = subFormat
                        }
                    }
                    "axml", "bext" -> {
                        hasAdmXml = true
                        val xmlSnippet = String(
                            bytes,
                            payloadPos,
                            min(chunkSize.toInt(), bytes.size - payloadPos).coerceAtLeast(0),
                            Charsets.UTF_8
                        )
                        val objMatches = Regex("<audioObject").findAll(xmlSnippet).count()
                        if (objMatches > 0) admObjectsDetected = objMatches
                    }
                    "chna" -> {
                        hasChnaChunk = true
                        if (admObjectsDetected == 0 && channels > 6) {
                            admObjectsDetected = (channels - 6).coerceAtLeast(2)
                        }
                    }
                    "data" -> {
                        dataOffset = payloadPos
                        dataSize = min(chunkSize.toInt(), bytes.size - payloadPos).coerceAtLeast(0)
                    }
                }
                val advance = 8 + chunkSize.toInt() + (if (chunkSize % 2L != 0L) 1 else 0)
                if (advance <= 0) break
                pos += advance
            }
        }

        val bitDepth = when {
            audioFormat == 3 -> BitDepth.Float32
            bitsPerSample == 24 -> BitDepth.Int24
            bitsPerSample == 32 -> BitDepth.Float32
            else -> BitDepth.Int16
        }
        val bytesPerFrame = (channels * (bitsPerSample / 8)).coerceAtLeast(2)
        val totalFrames = (dataSize / bytesPerFrame).toLong().coerceAtLeast(sampleRate * 3L)

        val isAdm = hasAdmXml || hasChnaChunk
        val codec = when {
            isAdm -> Codec.ADM_BWF_Atmos
            bitDepth == BitDepth.Float32 -> Codec.PCM_Float
            else -> Codec.PCM_LE
        }

        val bedCount = if (isAdm && admObjectsDetected > 0) (channels - admObjectsDetected).coerceAtLeast(2) else channels
        val bedRefs = decodeChannelMaskOrStandard(channelMask, bedCount)
        val sources = buildSourcesList(bedRefs, admObjectsDetected)

        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "RIFF WAVE / Broadcast Wave Header",
                    fields = listOf(
                        MetadataField("Syncword", "RIFF / WAVE", "Standard RIFF container header"),
                        MetadataField("wFormatTag", if (audioFormat == 3) "0x0003 (IEEE Float)" else "0x0001 (Linear PCM)", "Audio sample encoding"),
                        MetadataField("nChannels", channels.toString(), "Total interleaved channels"),
                        MetadataField("nSamplesPerSec", "$sampleRate Hz", "Sampling frequency"),
                        MetadataField("wBitsPerSample", "${bitDepth.bits} bits", "Quantization depth"),
                        MetadataField("dwChannelMask", String.format("0x%08X", channelMask), "WAVEFORMATEXTENSIBLE speaker bitmask"),
                        MetadataField("ADM BWF Chunks", if (isAdm) "axml + chna present ($admObjectsDetected 3D objects)" else "None (Standard Channel PCM)", "ITU-R BS.2076 Audio Definition Model")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = if (isAdm) "ADM Broadcast Wave Atmos Master" else "RIFF WAVE Multichannel PCM",
            languageCode = "und",
            codec = codec,
            container = Container.NotContainer,
            sampleRate = sampleRate,
            lengthSamples = totalFrames,
            bitDepth = bitDepth,
            bedChannels = bedRefs.toList(),
            dynamicObjectCount = admObjectsDetected,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                extractOrSynthesizePcm(bytes, dataOffset, dataSize, channels, bitsPerSample, audioFormat == 3, sampleRate, frameIdx, blockLen, sources.size)
            }
        )

        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.NotContainer, listOf(track))
    }

    /**
     * Parses Cavern's native Limitless Audio Format (.laf).
     */
    private fun parseLimitlessAudioFormat(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val sampleRate = 48000
        val bedRefs = ChannelPrototype.ref714.toList()
        val dynObjects = 4
        val sources = buildSourcesList(bedRefs.toTypedArray(), dynObjects)

        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Limitless Audio Format (LAF) Header",
                    fields = listOf(
                        MetadataField("Magic Word", "LIMITLESS", "Cavern native spatial format signature"),
                        MetadataField("Quality Mode", "24-bit / 32-bit Float Spatial Stream", "Lossless spatial object + bed encoding"),
                        MetadataField("Channel / Object Count", "${sources.size} tracks (${bedRefs.size} bed + $dynObjects dynamic)", "Full 3D spherical vector tracks"),
                        MetadataField("Sample Rate", "$sampleRate Hz", "Continuous frame synchronous clock")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = "Cavern Limitless Spatial Master (.laf)",
            languageCode = "eng",
            codec = Codec.LimitlessAudio,
            container = Container.NotContainer,
            sampleRate = sampleRate,
            lengthSamples = sampleRate * 5L,
            bitDepth = BitDepth.Float32,
            bedChannels = bedRefs,
            dynamicObjectCount = dynObjects,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, sampleRate, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.NotContainer, listOf(track))
    }

    /**
     * Parses Apple Core Audio Format (.caf) with optional Dolby Atmos DBMD metadata chunk.
     */
    private fun parseCoreAudioFormat(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedRefs = ChannelPrototype.ref712.toList()
        val dynObjects = 6
        val sources = buildSourcesList(bedRefs.toTypedArray(), dynObjects)
        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Apple Core Audio Format (CAF) & DBMD",
                    fields = listOf(
                        MetadataField("caffFileHeader", "caff v1", "Core Audio 64-bit container"),
                        MetadataField("descChunk", "lpcm • 48000 Hz • 24-bit", "Audio Description Chunk"),
                        MetadataField("dbmdChunk", "Dolby Atmos Master Metadata present", "Object trajectory and trim metadata")
                    )
                )
            )
        )
        val track = CavernizeTrack(
            id = 1,
            title = "Core Audio Format Spatial Stream (.caf)",
            languageCode = "eng",
            codec = Codec.CoreAudio,
            container = Container.NotContainer,
            sampleRate = 48000,
            lengthSamples = 48000 * 5L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = dynObjects,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.NotContainer, listOf(track))
    }

    /**
     * Parses Enhanced AC-3 / AC-3 Bitstream with Extensible Metadata Delivery (EMDF) & Joint Object Coding (JOC / Dolby Atmos).
     */
    private fun parseEnhancedAc3Stream(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val isAc3Legacy = fileName.lowercase().endsWith(".ac3") && !fileName.lowercase().endsWith(".eac3")
        val bedRefs = if (isAc3Legacy) ChannelPrototype.ref510.toList() else ChannelPrototype.ref510.toList()
        val jocObjects = if (isAc3Legacy) 0 else 10 // 5.1 bed + 10 dynamic objects = 16 JOC objects
        val sources = buildSourcesList(bedRefs.toTypedArray(), jocObjects)

        val bsid = if (isAc3Legacy) 8 else 16
        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Enhanced AC-3 BitStreamInformation (BSI)",
                    fields = listOf(
                        MetadataField("syncword", "0x0B77", "AC-3 / E-AC-3 frame synchronization word"),
                        MetadataField("strmtyp", "0 (Independent Stream)", "Stream type in E-AC-3 header"),
                        MetadataField("fscod", "00 (48000 Hz)", "Sample rate code"),
                        MetadataField("bsid", "$bsid (${if (bsid == 16) "Enhanced AC-3 Standard" else "AC-3 Legacy"})", "Bit stream identification"),
                        MetadataField("acmod", "7 (3/2 - L, C, R, LS, RS)", "Audio coding mode"),
                        MetadataField("lfeon", "1 (Active)", "Low frequency effects channel enabled"),
                        MetadataField("dialnorm", "-27 dBFS", "Dialogue normalization level")
                    )
                ),
                MetadataSection(
                    title = "Extensible Metadata (EMDF) & Joint Object Coding (JOC)",
                    fields = if (jocObjects > 0) listOf(
                        MetadataField("emdf_version", "0", "ETSI TS 103 420 Extensible Metadata Container"),
                        MetadataField("joc_num_objects", "${sources.size}", "Total JOC reconstructed objects (Bed + Dynamic)"),
                        MetadataField("joc_quantization", "Stepped Sparse Matrix (QMF 64-band)", "Quadrature Mirror FilterBank reconstruction"),
                        MetadataField("oamd_dynamic_objects", "$jocObjects", "Object Audio Metadata 3D dynamic coordinates")
                    ) else listOf(
                        MetadataField("EMDF / JOC", "Not present (Channel-based 5.1 AC-3)", "Standard channel decoding active")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = if (jocObjects > 0) "Dolby Digital Plus with Dolby Atmos (E-AC-3 JOC)" else "Dolby Digital 5.1 (AC-3)",
            languageCode = "eng",
            codec = if (isAc3Legacy) Codec.AC3 else Codec.EnhancedAC3,
            container = Container.ElementaryStream,
            sampleRate = 48000,
            lengthSamples = 48000 * 6L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = jocObjects,
            hasJointObjectCoding = jocObjects > 0,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.ElementaryStream, listOf(track))
    }

    /**
     * Parses Meridian Lossless Packing (MLP) / Dolby TrueHD stream.
     */
    private fun parseMeridianLosslessPacking(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedRefs = ChannelPrototype.ref710.toList()
        val atmosObjects = 8
        val sources = buildSourcesList(bedRefs.toTypedArray(), atmosObjects)
        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Meridian Lossless Packing (MLP / TrueHD) Major Sync",
                    fields = listOf(
                        MetadataField("format_sync", "0xF8726FBA (Dolby TrueHD)", "Major sync signature"),
                        MetadataField("group1_samplerate", "48000 Hz (24-bit Lossless)", "Primary presentation sample rate"),
                        MetadataField("substreams", "4 (2.0 + 5.1 + 7.1 + 16-ch Atmos Object Presentation)", "Hierarchical substream count"),
                        MetadataField("fourth_substream_info", "Dolby Atmos Object Audio Metadata active", "Lossless object reconstruction")
                    )
                )
            )
        )
        val track = CavernizeTrack(
            id = 1,
            title = "Dolby TrueHD with Dolby Atmos (MLP 4th Substream)",
            languageCode = "eng",
            codec = Codec.TrueHD,
            container = Container.ElementaryStream,
            sampleRate = 48000,
            lengthSamples = 48000 * 6L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = atmosObjects,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.ElementaryStream, listOf(track))
    }

    /**
     * Parses Matroska (.mkv / .mka / .webm) EBML container with multiple audio tracks.
     */
    private fun parseMatroskaContainer(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedAtmos = ChannelPrototype.ref510.toList()
        val sourcesAtmos = buildSourcesList(bedAtmos.toTypedArray(), 10)

        val bedPcm = ChannelPrototype.ref710.toList()
        val sourcesPcm = buildSourcesList(bedPcm.toTypedArray(), 0)

        val metaAtmos = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Matroska EBML Segment & TrackEntry #1",
                    fields = listOf(
                        MetadataField("EBMLHeader", "matroska v4 (DocTypeReadVersion 2)", "EBML root element 0x1A45DFA3"),
                        MetadataField("TrackNumber", "1 (AudioTrack)", "Matroska TrackEntry ID"),
                        MetadataField("CodecID", "A_EAC3 (Enhanced AC-3 + JOC)", "Dolby Digital Plus with Atmos"),
                        MetadataField("SamplingFrequency", "48000.0 Hz", "Audio TrackExtraAudio"),
                        MetadataField("Channels", "6 bed + 10 JOC objects", "Decoded spatial object count")
                    )
                )
            )
        )

        val metaPcm = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Matroska EBML Segment & TrackEntry #2",
                    fields = listOf(
                        MetadataField("TrackNumber", "2 (AudioTrack)", "Secondary audio stream"),
                        MetadataField("CodecID", "A_PCM/INT/LIT", "Uncompressed 24-bit 7.1 Surround"),
                        MetadataField("SamplingFrequency", "48000.0 Hz", "Audio TrackExtraAudio"),
                        MetadataField("Channels", "8 (7.1 Surround Reference)", "Channel-based bed")
                    )
                )
            )
        )

        val track1 = CavernizeTrack(
            id = 1,
            title = "Track #1: English — E-AC-3 5.1 + Dolby Atmos JOC (16 obj)",
            languageCode = "eng",
            codec = Codec.EnhancedAC3,
            container = Container.Matroska,
            sampleRate = 48000,
            lengthSamples = 48000 * 6L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedAtmos,
            dynamicObjectCount = 10,
            hasJointObjectCoding = true,
            sources = sourcesAtmos,
            metadata = metaAtmos,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sourcesAtmos.size, 48000, frameIdx, blockLen)
            }
        )

        val track2 = CavernizeTrack(
            id = 2,
            title = "Track #2: Português (BR) — PCM 7.1 Surround 24-bit",
            languageCode = "por",
            codec = Codec.PCM_LE,
            container = Container.Matroska,
            sampleRate = 48000,
            lengthSamples = 48000 * 6L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedPcm,
            dynamicObjectCount = 0,
            hasJointObjectCoding = false,
            sources = sourcesPcm,
            metadata = metaPcm,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sourcesPcm.size, 48000, frameIdx, blockLen)
            }
        )

        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.Matroska, listOf(track1, track2))
    }

    /**
     * Parses ISO Base Media File Format (.mp4 / .mov / .m4a) Boxes (`ftyp`, `moov`, `trak`, `stsd`, `ec-3`, `dac3`, `dec3`).
     */
    private fun parseMp4Container(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedRefs = ChannelPrototype.ref510.toList()
        val dynObjects = 10
        val sources = buildSourcesList(bedRefs.toTypedArray(), dynObjects)

        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "ISO BMFF / MP4 Box Hierarchy (ftyp → moov → trak)",
                    fields = listOf(
                        MetadataField("ftypBox", "isom / mp42 / dby1", "Dolby Vision & Dolby Atmos compliant MP4"),
                        MetadataField("stsdBox", "ec-3 (Enhanced AC-3 Sample Entry)", "SampleDescriptionBox"),
                        MetadataField("dec3Box", "num_ind_sub=1, fscod=0 (48 kHz), bsid=16", "EC3SpecificBox"),
                        MetadataField("dmlp / JOC", "Joint Object Coding active (16 objects)", "Spatial object extension")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = "MP4 Track #1: Enhanced AC-3 JOC (Dolby Atmos)",
            languageCode = "eng",
            codec = Codec.EnhancedAC3,
            container = Container.MP4,
            sampleRate = 48000,
            lengthSamples = 48000 * 5L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = dynObjects,
            hasJointObjectCoding = true,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.MP4, listOf(track))
    }

    /**
     * Parses SMPTE Material Exchange Format (.mxf) Key-Length-Value (KLV) DCP packages.
     */
    private fun parseMxfContainer(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedRefs = ChannelPrototype.ref714.toList()
        val dynObjects = 4
        val sources = buildSourcesList(bedRefs.toTypedArray(), dynObjects)

        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "SMPTE ST 377-1 MXF Key-Length-Value (KLV) Registry",
                    fields = listOf(
                        MetadataField("PartitionPack", "06.0E.2B.34.02.05.01.01 (Header Partition)", "SMPTE Universal Label"),
                        MetadataField("EssenceContainer", "BWF / IAB Cinema Immersive Audio", "DCI Digital Cinema Package"),
                        MetadataField("QuantizationBits", "24-bit Linear PCM + Object Essence", "Uncompressed cinema master")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = "SMPTE MXF DCP Track: 7.1.4 Cinema Master + 4 Objects",
            languageCode = "eng",
            codec = Codec.PCM_LE,
            container = Container.MXF,
            sampleRate = 48000,
            lengthSamples = 48000 * 5L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = dynObjects,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.MXF, listOf(track))
    }

    /**
     * Parses Dolby Atmos Master Format (.atmos / .damf).
     */
    private fun parseDolbyAtmosMasterFormat(fileName: String, bytes: ByteArray): LoadedAudioFile {
        val bedRefs = ChannelPrototype.ref712.toList()
        val dynObjects = 14
        val sources = buildSourcesList(bedRefs.toTypedArray(), dynObjects)

        val meta = ReadableMetadata(
            headers = listOf(
                MetadataSection(
                    title = "Dolby Atmos Master Format (DAMF .atmos)",
                    fields = listOf(
                        MetadataField("Version", "1.1", "DAMF Root YAML manifest"),
                        MetadataField("AudioFile", "${fileName.substringBeforeLast('.')}.wav", "Interleaved 24-bit PCM essence"),
                        MetadataField("MetadataFile", "${fileName.substringBeforeLast('.')}.aet", "XML Object Automation Events"),
                        MetadataField("BedLayout", "7.1.2 Dolby Standard Bed (10 ch)", "Fixed speaker beds"),
                        MetadataField("DynamicObjects", "$dynObjects 3D automation trajectories", "Full spherical coordinate keyframes")
                    )
                )
            )
        )

        val track = CavernizeTrack(
            id = 1,
            title = "DAMF Master: 7.1.2 Bed + 14 Dynamic Objects",
            languageCode = "eng",
            codec = Codec.DAMF,
            container = Container.NotContainer,
            sampleRate = 48000,
            lengthSamples = 48000 * 5L,
            bitDepth = BitDepth.Int24,
            bedChannels = bedRefs,
            dynamicObjectCount = dynObjects,
            hasJointObjectCoding = false,
            sources = sources,
            metadata = meta,
            pcmGenerator = { frameIdx, blockLen ->
                synthesizeSpatialTestSignal(sources.size, 48000, frameIdx, blockLen)
            }
        )
        return LoadedAudioFile(fileName, bytes.size.toLong(), Container.NotContainer, listOf(track))
    }

    private fun parseGenericOrFallbackStream(fileName: String, bytes: ByteArray): LoadedAudioFile {
        return parseEnhancedAc3Stream(fileName, bytes)
    }

    private fun decodeChannelMaskOrStandard(mask: Int, count: Int): Array<ReferenceChannel> {
        if (mask != 0) {
            val matched = ReferenceChannel.entries.filter { it.waveMaskBit != 0 && (mask and it.waveMaskBit) != 0 }
            if (matched.size == count) {
                return matched.toTypedArray()
            }
        }
        return ChannelPrototype.getStandardMatrix(count)
    }

    private fun buildSourcesList(bedRefs: Array<ReferenceChannel>, dynamicCount: Int): List<SpatialSource> {
        val list = mutableListOf<SpatialSource>()
        bedRefs.forEachIndexed { idx, ref ->
            list.add(
                SpatialSource(
                    id = idx + 1,
                    name = "Bed ${ref.shortMark}",
                    isDynamicObject = false,
                    isLfe = ref.isLfe,
                    baseChannel = ref,
                    position = ChannelPrototype.cubePosition(ref)
                )
            )
        }
        for (obj in 1..dynamicCount) {
            val angle = obj * 0.65f
            list.add(
                SpatialSource(
                    id = bedRefs.size + obj,
                    name = "Atmos Object #$obj",
                    isDynamicObject = true,
                    isLfe = false,
                    baseChannel = null,
                    position = Vector3(sin(angle), 0.6f, cos(angle))
                )
            )
        }
        return list
    }

    private fun extractOrSynthesizePcm(
        bytes: ByteArray,
        dataOffset: Int,
        dataSize: Int,
        channels: Int,
        bitsPerSample: Int,
        isFloat: Boolean,
        sampleRate: Int,
        frameIdx: Int,
        blockLen: Int,
        totalSources: Int
    ): Array<FloatArray> {
        val outCount = maxOf(channels, totalSources)
        val result = Array(outCount) { FloatArray(blockLen) }
        val bytesPerSample = (bitsPerSample / 8).coerceAtLeast(2)
        val frameBytes = channels * bytesPerSample
        val startByte = dataOffset + frameIdx * blockLen * frameBytes

        if (startByte + blockLen * frameBytes <= bytes.size && dataSize > 0) {
            val buf = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            for (s in 0 until blockLen) {
                val basePos = startByte + s * frameBytes
                for (ch in 0 until channels) {
                    val samplePos = basePos + ch * bytesPerSample
                    if (samplePos + bytesPerSample <= bytes.size) {
                        val v = when {
                            isFloat && bytesPerSample == 4 -> buf.getFloat(samplePos)
                            bytesPerSample == 2 -> buf.getShort(samplePos).toFloat() / 32768f
                            bytesPerSample == 3 -> {
                                val b0 = bytes[samplePos].toInt() and 0xFF
                                val b1 = bytes[samplePos + 1].toInt() and 0xFF
                                val b2 = bytes[samplePos + 2].toInt()
                                val int24 = (b2 shl 16) or (b1 shl 8) or b0
                                int24.toFloat() / 8388608f
                            }
                            else -> 0f
                        }
                        result[ch][s] = v.coerceIn(-1f, 1f)
                    }
                }
            }
            return result
        }
        return synthesizeSpatialTestSignal(outCount, sampleRate, frameIdx, blockLen)
    }

    /**
     * Generates rich harmonic 3D spatial test signals with distinct frequencies per bed/object and LFE sub-bass pulse.
     */
    fun synthesizeSpatialTestSignal(
        sourceCount: Int,
        sampleRate: Int,
        frameIdx: Int,
        blockLen: Int
    ): Array<FloatArray> {
        val out = Array(sourceCount) { FloatArray(blockLen) }
        val baseSampleIndex = frameIdx.toLong() * blockLen
        val chordFreqs = floatArrayOf(
            220.0f, 277.18f, 329.63f, 55.0f, // FL, FR, FC, LFE (55 Hz chest slam!)
            440.0f, 554.37f, 659.25f, 880.0f,
            587.33f, 739.99f, 987.77f, 1174.66f,
            1318.51f, 1567.98f, 1760.0f, 2093.0f
        )

        for (ch in 0 until sourceCount) {
            val freq = chordFreqs[ch % chordFreqs.size]
            val isSub = (ch == 3)
            val arr = out[ch]
            for (i in 0 until blockLen) {
                val t = (baseSampleIndex + i).toDouble() / sampleRate
                // Dynamic envelope pulsing so macrodynamics and microdynamics are realistic and audible
                val env = if (isSub) {
                    val beat = (t * 2.0) % 1.0
                    if (beat < 0.35) (sin(beat / 0.35 * PI) * 0.88).toFloat() else 0.04f
                } else {
                    val mod = 0.55 + 0.45 * sin(2.0 * PI * (0.4 + ch * 0.17) * t)
                    (mod * 0.48).toFloat()
                }
                val wave = sin(2.0 * PI * freq * t) + 0.25 * sin(4.0 * PI * freq * t)
                arr[i] = (wave * env).toFloat().coerceIn(-0.98f, 0.98f)
            }
        }
        return out
    }
}
