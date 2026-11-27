using System;
using System.Collections.Generic;
using System.Linq;

using Cavern.Channels;
using Cavern.Format.Common;
using Cavern.Format.Decoders.EnhancedAC3;
using Cavern.Format.Transcoders;
using Cavern.Format.Utilities;
using Cavern.Utilities;

namespace Cavern.Format.Decoders {
    /// <summary>
    /// Converts an Enhanced AC-3 bitstream to raw samples.
    /// </summary>
    public class EnhancedAC3Decoder : FrameBasedDecoder, IMetadataSupplier {
        /// <summary>
        /// Lightweight frame-table entry mapping a decoded timeslot to its exact bitstream byte offset.
        /// </summary>
        struct SeekFrameEntry {
            public long ByteOffset;
            public long SampleOffset;
            public int SampleCount;
            public int ByteLength;
        }

        /// <summary>
        /// The stream is coded in the Enhanced version of AC-3.
        /// </summary>
        public bool Enhanced => header.Decoder >= Transcoders.EnhancedAC3.Decoders.LG_EAC3;

        /// <summary>
        /// True if the stream has reached its end.
        /// </summary>
        public bool Finished { get; private set; }

        /// <summary>
        /// Samples in each decoded frame.
        /// </summary>
        public int FrameSize => header.Blocks * samplesPerBlock;

        /// <summary>
        /// Number of E-AC-3 timeslots currently indexed in the VBR frame seek table.
        /// </summary>
        public int IndexedFrameCount => seekIndex.Count;

        /// <summary>
        /// Decoded Dolby Dialogue Normalization level in LKFS/LUFS (-31..-1 LUFS per ATSC A/52 &amp; ETSI TS 102 366).
        /// </summary>
        public float DialnormLufs => header.DialnormLufs;

        /// <summary>
        /// True when E-AC-3 channel coupling was active in the most recently decoded audio frame.
        /// </summary>
        public bool CouplingActive { get; private set; }

        /// <summary>
        /// True when E-AC-3 Spectral Extension (SPX) was active in the most recently decoded audio frame.
        /// </summary>
        public bool SpxActive { get; private set; }

        /// <summary>
        /// Live channel coupling start frequency in Hz across active substreams (0 when inactive).
        /// </summary>
        public float CouplingStartHz { get; private set; }

        /// <summary>
        /// Live Spectral Extension (SPX) start frequency in Hz across active substreams (0 when inactive).
        /// </summary>
        public float SpxStartHz { get; private set; }

        /// <summary>
        /// Header data container and reader.
        /// </summary>
        readonly EnhancedAC3Header header = new EnhancedAC3Header();

        /// <summary>
        /// Independently decoded substreams.
        /// </summary>
        readonly Dictionary<int, EnhancedAC3Body> bodies = new Dictionary<int, EnhancedAC3Body>();

        /// <summary>
        /// Rendered samples for each channel.
        /// </summary>
        readonly Dictionary<ReferenceChannel, float[]> outputs = new Dictionary<ReferenceChannel, float[]>();

        /// <summary>
        /// Indexed VBR frame byte/sample offsets for accurate <see cref="Seek(long)"/> without linear re-decode.
        /// </summary>
        readonly List<SeekFrameEntry> seekIndex = new List<SeekFrameEntry>();

        /// <summary>
        /// Known raw stream size in bytes, or -1 for container/live streams.
        /// </summary>
        readonly long fileSize = -1;

        /// <summary>
        /// Bytes per audio frame (last observed timeslot size for fallback calculations).
        /// </summary>
        long frameSize;

        /// <summary>
        /// Cumulative byte position read from <see cref="Decoder.reader"/>.
        /// </summary>
        long streamByteOffset;

        /// <summary>
        /// Stream byte offset where the most recently prefetched <see cref="ReadHeader"/> syncframe started.
        /// </summary>
        long pendingSyncByteStart;

        /// <summary>
        /// Sample count per channel of the most recently prefetched <see cref="ReadHeader"/> syncframe.
        /// </summary>
        int pendingSyncSamples;

        /// <summary>
        /// Sample index per channel where the next decoded timeslot begins.
        /// </summary>
        long currentTimeslotSampleStart;

        /// <summary>
        /// Frame index of the next timeslot to decode.
        /// </summary>
        int currentTimeslotIndex;

        /// <summary>
        /// Forces <see cref="DecodeFrame"/> to re-read the frame header after <see cref="Seek(long)"/>.
        /// </summary>
        bool needsHeaderResync;

        /// <summary>
        /// True when <see cref="seekIndex"/> has indexed through the end of the raw bitstream.
        /// </summary>
        bool indexComplete;

        /// <summary>
        /// Highest sample offset covered by <see cref="seekIndex"/>.
        /// </summary>
        long totalIndexedSamples;

        /// <summary>
        /// Highest stream byte offset covered by <see cref="seekIndex"/> (start of the next unindexed timeslot).
        /// </summary>
        long totalIndexedBytes;

        /// <summary>
        /// True after <see cref="Seek(long)"/> so <see cref="DecodeBlock"/> bypasses stale pre-seek <see cref="FrameBasedDecoder"/> cache.
        /// </summary>
        bool usePostSeekBuffer;

        /// <summary>
        /// Reusable frame sample buffer active after <see cref="Seek(long)"/>.
        /// </summary>
        float[] postSeekBuffer = Array.Empty<float>();

        /// <summary>
        /// Current read index inside <see cref="postSeekBuffer"/>.
        /// </summary>
        int postSeekPos;

        /// <summary>
        /// Valid sample count inside <see cref="postSeekBuffer"/>.
        /// </summary>
        int postSeekAvail;

        /// <summary>
        /// Remaining intra-frame interleaved samples to discard right after a seek.
        /// </summary>
        int skipPostSeekSamples;

        /// <summary>
        /// True when the most recent <see cref="DecodeFrame"/> call produced valid decoded audio samples.
        /// </summary>
        bool decodedFrameValid;

        /// <summary>
        /// Auxillary metadata parsed for the last decoded frame.
        /// </summary>
        internal ExtensibleMetadataDecoder Extensions { get; private set; } = new ExtensibleMetadataDecoder();

        /// <summary>
        /// Reusable output sample array.
        /// </summary>
        float[] outCache = Array.Empty<float>();

        /// <summary>
        /// Converts an Enhanced AC-3 bitstream to raw samples.
        /// </summary>
        public EnhancedAC3Decoder(BlockBuffer<byte> reader) : base(reader) {
            Length = -1;
            Bits = BitDepth.Int24;
        }

        /// <summary>
        /// Converts an Enhanced AC-3 bitstream to raw samples. When the file size is known and the stream is seekable,
        /// builds a lightweight VBR frame-offset seek table for accurate <see cref="Seek(long)"/> and exact duration.
        /// </summary>
        /// <param name="reader">Accesses the linear E-AC-3 bitstream</param>
        /// <param name="fileSize">Length of the E-AC-3 bitstream</param>
        public EnhancedAC3Decoder(BlockBuffer<byte> reader, long fileSize) : this(reader) {
            this.fileSize = fileSize;
            if (fileSize <= 0) {
                Length = -1;
                return;
            }
            if (Finished) {
                indexComplete = true;
                Length = totalIndexedSamples;
                return;
            }
            if (reader != null && reader.Seekable) {
                long resumeByteOffset = streamByteOffset;
                bool scanAll = fileSize <= eagerFullIndexMaxBytes;
                ScanFramesFromPrefetchedHeader(
                    pendingSyncByteStart,
                    pendingSyncSamples,
                    streamByteOffset,
                    scanAll ? long.MaxValue : lazyPrefixIndexFrames);
                reader.Seek(resumeByteOffset);
                if (indexComplete) {
                    Length = totalIndexedSamples;
                    return;
                }
                if (totalIndexedBytes > 0 && totalIndexedSamples > 0) {
                    double avgSamplesPerByte = totalIndexedSamples / (double)totalIndexedBytes;
                    long estFrames = Math.Max(1L, (long)Math.Round(fileSize * avgSamplesPerByte / Math.Max(1, FrameSize)));
                    Length = estFrames * FrameSize;
                    return;
                }
            }
            Length = frameSize > 0 ? fileSize / frameSize * FrameSize : -1;
        }

        /// <summary>
        /// Get the bed channels.
        /// </summary>
        public ReferenceChannel[] GetChannels() {
            ReferenceChannel[] result = new ReferenceChannel[outputs.Count];
            outputs.Keys.CopyTo(result, 0);
            return result;
        }

        /// <inheritdoc/>
        public override void DecodeBlock(float[] target, long from, long to) {
            if (!usePostSeekBuffer) {
                base.DecodeBlock(target, from, to);
                if (Position < 0) {
                    Position = 0;
                }
                Position += (to - from) / Math.Max(1, ChannelCount);
                return;
            }

            long remaining = to - from;
            long written = 0;
            while (written < remaining) {
                if (postSeekPos >= postSeekAvail) {
                    float[] frame = DecodeFrame();
                    int frameLen = !decodedFrameValid || frame == null ? 0 : frame.Length;
                    if (frameLen == 0) {
                        Array.Clear(target, (int)(from + written), (int)(remaining - written));
                        return;
                    }
                    if (postSeekBuffer.Length < frameLen) {
                        postSeekBuffer = new float[frameLen];
                    }
                    Array.Copy(frame, 0, postSeekBuffer, 0, frameLen);
                    postSeekAvail = frameLen;
                    postSeekPos = skipPostSeekSamples > 0 ? Math.Min(frameLen, skipPostSeekSamples) : 0;
                    skipPostSeekSamples = 0;
                    if (postSeekPos >= postSeekAvail) {
                        continue;
                    }
                }

                int copy = (int)Math.Min(postSeekAvail - postSeekPos, remaining - written);
                Array.Copy(postSeekBuffer, postSeekPos, target, (int)(from + written), copy);
                postSeekPos += copy;
                written += copy;
            }
            Position += remaining / Math.Max(1, ChannelCount);
        }

        /// <summary>
        /// Decode a new frame if the cached samples are already fetched.
        /// </summary>
        protected override float[] DecodeFrame() {
            decodedFrameValid = false;
            if (outputs.Count == 0 || needsHeaderResync) {
                needsHeaderResync = false;
                ReadHeader();
            }

            if (Finished) {
                outCache.Clear();
                return outCache;
            }

            long timeslotByteStart = pendingSyncByteStart;
            int timeslotSamples = pendingSyncSamples > 0 ? pendingSyncSamples : FrameSize;
            bool frameCplActive = false;
            bool frameSpxActive = false;
            float frameMinCplHz = float.MaxValue;
            float frameMinSpxHz = float.MaxValue;
            do {
                if (Finished) {
                    outCache.Clear();
                    return outCache;
                }

                EnhancedAC3Body body = bodies[header.SubstreamID];
                body.Update();
                if (body.CouplingActive && body.CouplingStartHz > 0f) {
                    frameCplActive = true;
                    if (body.CouplingStartHz < frameMinCplHz) {
                        frameMinCplHz = body.CouplingStartHz;
                    }
                }
                if (body.SpxActive && body.SpxStartHz > 0f) {
                    frameSpxActive = true;
                    if (body.SpxStartHz < frameMinSpxHz) {
                        frameMinSpxHz = body.SpxStartHz;
                    }
                }
                for (int i = 0, c = body.Channels.Count; i < c; ++i) {
                    outputs[body.Channels[i]] = body.FrameResult[i];
                }
                if (header.LFE) {
                    outputs[ReferenceChannel.ScreenLFE] = body.LFEResult;
                }

                Extensions.Decode(body.GetAuxData());
                ReadHeader();
            } while (!Finished && header.SubstreamID != 0);

            CouplingActive = frameCplActive;
            CouplingStartHz = frameCplActive ? frameMinCplHz : 0f;
            SpxActive = frameSpxActive;
            SpxStartHz = frameSpxActive ? frameMinSpxHz : 0f;

            int outLength = outputs.Count * timeslotSamples;
            if (outCache.Length != outLength) {
                outCache = new float[outLength];
            }

            int channelIndex = 0;
            IOrderedEnumerable<KeyValuePair<ReferenceChannel, float[]>> orderedChannels = outputs.OrderBy(x => x.Key);
            foreach (KeyValuePair<ReferenceChannel, float[]> channel in orderedChannels) {
                WaveformUtils.Insert(channel.Value, outCache, channelIndex++, outputs.Count);
            }

            long timeslotByteEnd = Finished ? streamByteOffset : pendingSyncByteStart;
            long timeslotBytes = timeslotByteEnd - timeslotByteStart;
            if (timeslotBytes > 0) {
                frameSize = timeslotBytes;
            }
            RecordIndexedFrame(currentTimeslotIndex, timeslotByteStart, currentTimeslotSampleStart, timeslotSamples, (int)Math.Max(0L, timeslotBytes));
            if (Finished) {
                indexComplete = true;
            }
            currentTimeslotIndex++;
            currentTimeslotSampleStart += timeslotSamples;

            ChannelCount = outputs.Count;
            SampleRate = header.SampleRate;
            decodedFrameValid = true;
            return outCache;
        }

        /// <summary>
        /// Reads all metadata for the next frame and prepares audio decoding.
        /// </summary>
        /// <remarks>This decoder has to read the beginning of the next frame to know if it's a beginning.</remarks>
        void ReadHeader() {
            if (reader.Readable) {
                long syncStart = streamByteOffset;
                BitExtractor extractor = header.Decode(reader);
                if (extractor == null || !extractor.Readable) {
                    Finished = true;
                    return;
                }
                long syncBytes = header.WordsPerSyncframe * 2L;
                pendingSyncByteStart = syncStart;
                pendingSyncSamples = header.Blocks * samplesPerBlock;
                streamByteOffset = syncStart + syncBytes;
                if (!bodies.ContainsKey(header.SubstreamID)) {
                    bodies[header.SubstreamID] = new EnhancedAC3Body(header);
                }
                bodies[header.SubstreamID].PrepareUpdate(extractor);
            } else {
                Finished = true;
            }
        }

        /// <summary>
        /// Start the following reads from the selected sample using the indexed VBR frame-offset table.
        /// </summary>
        /// <param name="sample">The selected sample, for a single channel</param>
        public override void Seek(long sample) {
            if (Length == -1) {
                throw new NotImplementedException(trackSeekError);
            }

            long targetSample = sample < 0 ? 0 : sample;
            if (!indexComplete && targetSample >= totalIndexedSamples && reader != null && reader.Seekable) {
                ScanForwardFromIndexedTail(targetSample);
                if (indexComplete && totalIndexedSamples > 0) {
                    Length = totalIndexedSamples;
                }
            }

            if (seekIndex.Count == 0) {
                long targetFrame = frameSize > 0 ? targetSample / samplesPerBlock / Math.Max(1, header.Blocks) : 0;
                reader.Seek(targetFrame * frameSize);
                bodies.Clear();
                Finished = false;
                needsHeaderResync = true;
                usePostSeekBuffer = true;
                postSeekPos = 0;
                postSeekAvail = 0;
                skipPostSeekSamples = 0;
                Position = targetSample;
                return;
            }

            if (indexComplete && targetSample >= totalIndexedSamples) {
                Finished = true;
                usePostSeekBuffer = true;
                postSeekPos = 0;
                postSeekAvail = 0;
                skipPostSeekSamples = 0;
                Position = totalIndexedSamples;
                return;
            }

            int idx = FindSeekIndexEntry(targetSample);
            SeekFrameEntry entry = seekIndex[idx];
            reader.Seek(entry.ByteOffset);
            bodies.Clear();
            Finished = false;
            needsHeaderResync = true;
            usePostSeekBuffer = true;
            postSeekPos = 0;
            postSeekAvail = 0;
            skipPostSeekSamples = (int)Math.Max(0L, targetSample - entry.SampleOffset) * Math.Max(1, ChannelCount);
            streamByteOffset = entry.ByteOffset;
            pendingSyncByteStart = entry.ByteOffset;
            pendingSyncSamples = entry.SampleCount;
            currentTimeslotIndex = idx;
            currentTimeslotSampleStart = entry.SampleOffset;
            Position = targetSample;
        }

        void RecordIndexedFrame(int frameIndex, long byteOffset, long sampleOffset, int sampleCount, int byteLength) {
            if (frameIndex == seekIndex.Count) {
                seekIndex.Add(new SeekFrameEntry {
                    ByteOffset = byteOffset,
                    SampleOffset = sampleOffset,
                    SampleCount = sampleCount,
                    ByteLength = byteLength
                });
                long nextSamples = sampleOffset + sampleCount;
                long nextBytes = byteOffset + byteLength;
                if (nextSamples > totalIndexedSamples) {
                    totalIndexedSamples = nextSamples;
                }
                if (nextBytes > totalIndexedBytes) {
                    totalIndexedBytes = nextBytes;
                }
            }
        }

        int FindSeekIndexEntry(long targetSample) {
            int lo = 0;
            int hi = seekIndex.Count - 1;
            int best = 0;
            while (lo <= hi) {
                int mid = lo + ((hi - lo) >> 1);
                if (seekIndex[mid].SampleOffset <= targetSample) {
                    best = mid;
                    lo = mid + 1;
                } else {
                    hi = mid - 1;
                }
            }
            return best;
        }

        void ScanFramesFromPrefetchedHeader(long firstTimeslotStart, int firstTimeslotSamples, long scanPos, long maxFrames) {
            long curTimeslotStart = firstTimeslotStart;
            int curTimeslotSamples = firstTimeslotSamples > 0 ? firstTimeslotSamples : FrameSize;
            long curSampleOffset = totalIndexedSamples;
            int frameIdx = seekIndex.Count;
            long scanned = 0;

            while (scanned < maxFrames && TryReadSyncHeader(reader, out int wordsPerSyncframe, out int blocks, out int substreamId)) {
                long syncBytes = wordsPerSyncframe * 2L;
                int skip = (int)syncBytes - Transcoders.EnhancedAC3.mustDecode;
                if (!SkipBytes(reader, skip)) {
                    scanPos += Transcoders.EnhancedAC3.mustDecode;
                    RecordIndexedFrame(frameIdx++, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
                    indexComplete = true;
                    return;
                }
                if (substreamId == 0) {
                    RecordIndexedFrame(frameIdx++, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
                    curSampleOffset += curTimeslotSamples;
                    curTimeslotStart = scanPos;
                    curTimeslotSamples = blocks * samplesPerBlock;
                    scanned++;
                    if (scanned >= maxFrames) {
                        return;
                    }
                }
                scanPos += syncBytes;
            }

            if (curTimeslotStart < scanPos) {
                RecordIndexedFrame(frameIdx, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
            }
            indexComplete = true;
        }

        void ScanForwardFromIndexedTail(long targetSample) {
            if (indexComplete || reader == null || !reader.Seekable) {
                return;
            }
            reader.Seek(totalIndexedBytes);
            long scanPos = totalIndexedBytes;
            long curTimeslotStart = -1;
            int curTimeslotSamples = 0;
            long curSampleOffset = totalIndexedSamples;
            int frameIdx = seekIndex.Count;

            while (TryReadSyncHeader(reader, out int wordsPerSyncframe, out int blocks, out int substreamId)) {
                long syncBytes = wordsPerSyncframe * 2L;
                int skip = (int)syncBytes - Transcoders.EnhancedAC3.mustDecode;
                if (!SkipBytes(reader, skip)) {
                    if (curTimeslotStart >= 0) {
                        scanPos += Transcoders.EnhancedAC3.mustDecode;
                        RecordIndexedFrame(frameIdx++, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
                    }
                    indexComplete = true;
                    return;
                }
                if (substreamId == 0) {
                    if (curTimeslotStart >= 0) {
                        RecordIndexedFrame(frameIdx++, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
                        curSampleOffset += curTimeslotSamples;
                        if (curSampleOffset > targetSample) {
                            return;
                        }
                    }
                    curTimeslotStart = scanPos;
                    curTimeslotSamples = blocks * samplesPerBlock;
                }
                scanPos += syncBytes;
            }

            if (curTimeslotStart >= 0 && curTimeslotStart < scanPos) {
                RecordIndexedFrame(frameIdx, curTimeslotStart, curSampleOffset, curTimeslotSamples, (int)Math.Max(0L, scanPos - curTimeslotStart));
            }
            indexComplete = true;
        }

        static bool TryReadSyncHeader(BlockBuffer<byte> source, out int wordsPerSyncframe, out int blocks, out int substreamId) {
            wordsPerSyncframe = 0;
            blocks = 0;
            substreamId = 0;
            if (source == null || !source.Readable) {
                return false;
            }
            byte[] b = source.Read(Transcoders.EnhancedAC3.mustDecode);
            if (b == null || b.Length < Transcoders.EnhancedAC3.mustDecode) {
                return false;
            }
            int sync = (b[0] << 8) | b[1];
            if (sync != Transcoders.EnhancedAC3.syncWord) {
                return false;
            }

            int bsid = (b[5] >> 3) & 0x1F;
            if (!Enum.IsDefined(typeof(Transcoders.EnhancedAC3.Decoders), bsid)) {
                return false;
            }
            Transcoders.EnhancedAC3.Decoders dec = (Transcoders.EnhancedAC3.Decoders)bsid;
            int fscod = (b[4] >> 6) & 0x03;
            if (fscod == 3) {
                return false;
            }

            if (dec >= Transcoders.EnhancedAC3.Decoders.LG_EAC3) {
                int strmtyp = (b[2] >> 6) & 0x03;
                if (strmtyp == (int)Transcoders.EnhancedAC3.StreamTypes.Reserved) {
                    return false;
                }
                substreamId = (b[2] >> 3) & 0x07;
                if (strmtyp == (int)Transcoders.EnhancedAC3.StreamTypes.Dependent) {
                    substreamId += 8;
                }
                wordsPerSyncframe = (((b[2] & 0x07) << 8) | b[3]) + 1;
                blocks = Transcoders.EnhancedAC3.numberOfBlocks[(b[4] >> 4) & 0x03];
            } else {
                substreamId = 0;
                blocks = 6;
                int frmsizecod = b[4] & 0x3F;
                int idx = frmsizecod >> 1;
                if (idx >= Transcoders.EnhancedAC3.frameSizes.Length) {
                    return false;
                }
                wordsPerSyncframe = Transcoders.EnhancedAC3.frameSizes[idx];
                if (fscod == 1) {
                    wordsPerSyncframe = wordsPerSyncframe * 1393 / 1280;
                    if ((frmsizecod & 1) == 1) {
                        ++wordsPerSyncframe;
                    }
                } else if (fscod == 2) {
                    wordsPerSyncframe += wordsPerSyncframe >> 1;
                }
            }
            return wordsPerSyncframe * 2 >= Transcoders.EnhancedAC3.mustDecode;
        }

        static bool SkipBytes(BlockBuffer<byte> source, int byteCount) {
            if (byteCount <= 0) {
                return true;
            }
            const int chunk = Transcoders.EnhancedAC3.mustDecode;
            while (byteCount >= chunk) {
                if (source.Read(chunk) == null) {
                    return false;
                }
                byteCount -= chunk;
            }
            if (byteCount > 0 && source.Read(byteCount) == null) {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the metadata for this codec in a human-readable format.
        /// </summary>
        public ReadableMetadata GetMetadata() {
            IEnumerable<ReadableMetadataHeader> headers = header.GetMetadata().Headers;
            if (Extensions.OAMD != null) {
                headers = headers.Concat(Extensions.OAMD.GetMetadata().Headers);
            }
            if (Extensions.JOC != null) {
                headers = headers.Concat(Extensions.JOC.GetMetadata().Headers);
            }
            return new ReadableMetadata(headers.ToList());
        }

        /// <summary>
        /// Each (E-)AC-3 block is 256 samples.
        /// </summary>
        const int samplesPerBlock = 256;

        /// <summary>
        /// Eager full-index byte limit for raw E-AC-3 streams (16 MiB); larger streams index lazily on demand.
        /// </summary>
        const long eagerFullIndexMaxBytes = 16L * 1024 * 1024;

        /// <summary>
        /// Initial prefix timeslot budget scanned on >16 MiB raw E-AC-3 streams for duration estimation.
        /// </summary>
        const int lazyPrefixIndexFrames = 256;

        /// <summary>
        /// Exception message when trying to seek a track.
        /// </summary>
        const string trackSeekError = "For seeking in an (E-)AC-3 track, use the container's Seek function.";
    }
}