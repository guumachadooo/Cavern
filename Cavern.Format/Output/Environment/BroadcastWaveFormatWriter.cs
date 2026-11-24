using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Xml;

using Cavern.Format.Consts;
using Cavern.Format.Renderers;
using Cavern.Format.Transcoders;
using Cavern.Format.Transcoders.AudioDefinitionModelElements;
using Cavern.Utilities;

namespace Cavern.Format.Environment {
    /// <summary>
    /// Object-based exporter of a listening environment to Audio Definition Model Broadcast Wave Format.
    /// </summary>
    public class BroadcastWaveFormatWriter : EnvironmentWriter {
        /// <summary>
        /// Reports the progress of the final AXML export [0;1].
        /// </summary>
        public Action<double> FinalFeedback;

        /// <summary>
        /// <see cref="FinalFeedback"/> will report numbers from this to 1.
        /// </summary>
        public double FinalFeedbackStart;

        /// <summary>
        /// The main PCM exporter.
        /// </summary>
        protected RIFFWaveWriter Output { get; private set; }

        /// <summary>
        /// Recorded movement path of all sources.
        /// </summary>
        protected List<ADMBlockFormat>[] Movements { get; private set; }

        /// <summary>
        /// When not null, writes the AXML to this separate file.
        /// </summary>
        Stream admWriter;

        /// <summary>
        /// Total samples written to the export file.
        /// </summary>
        long samplesWritten;

        /// <summary>
        /// Object-based exporter of a listening environment to Audio Definition Model Broadcast Wave Format.
        /// When an XML path is received, the waveform and the ADM will be written to separate files.
        /// </summary>
        public BroadcastWaveFormatWriter(Stream writer, Listener source, long length, BitDepth bits) :
            base(writer, source, length, bits) { }

        /// <summary>
        /// Object-based exporter of a listening environment to Audio Definition Model Broadcast Wave Format.
        /// When an XML path is received, the waveform and the ADM will be written to separate files.
        /// </summary>
        public BroadcastWaveFormatWriter(string path, Listener source, long length, BitDepth bits) :
            base(path, source, length, bits) { }

        /// <summary>
        /// Export the next frame of the <see cref="Source"/>.
        /// </summary>
        public override void WriteNextFrame() {
            if (Output == null) {
                CreateFile();
            }

            float[] result = GetInterlacedPCMOutput();
            long writable = Output.Length - samplesWritten;
            if (writable > 0) {
                Output.WriteBlock(result, 0, Math.Min(Source.UpdateRate, writable) * Output.ChannelCount);
            }

            Vector3 scaling = new Vector3(1) / Listener.EnvironmentSize;
            double timeScaling = 1.0 / Source.SampleRate;
            ADMTimeSpan updateTime = new ADMTimeSpan(Source.UpdateRate * timeScaling),
                newOffset = new ADMTimeSpan(samplesWritten * timeScaling);

            int sourceIndex = 0;
            foreach (Source source in Source.ActiveSources) {
                List<ADMBlockFormat> movement = Movements[sourceIndex];
                int size = movement.Count;
                Vector3 scaledPosition = Vector3.Clamp(source.Position * scaling, minusOne, Vector3.One);
                if (size == 0 || movement[size - 1].Position != scaledPosition) {
                    bool replace = false;
                    if (size > 1) {
                        replace = IsGeodesicDecimatable(
                            movement[size - 2],
                            movement[size - 1],
                            scaledPosition,
                            newOffset.TotalSeconds);
                    }

                    if (replace) {
                        ADMBlockFormat prev = movement[size - 1];
                        prev.Position = scaledPosition;
                        prev.Duration += updateTime;
                        if (!prev.Interpolation.IsZero()) {
                            prev.Interpolation += updateTime;
                        }
                    } else {
                        bool jumpCut = false;
                        if (size != 0) {
                            ADMBlockFormat prev = movement[size - 1];
                            prev.Duration = newOffset - prev.Offset;
                            jumpCut = ShouldJumpCut(prev.Position, scaledPosition);
                        }
                        movement.Add(new ADMBlockFormat {
                            Position = scaledPosition,
                            Offset = newOffset,
                            Duration = updateTime,
                            Interpolation = jumpCut ? ADMTimeSpan.Zero : updateTime
                        });
                    }
                } else {
                    movement[size - 1].Duration += updateTime;
                }
                ++sourceIndex;
            }
            samplesWritten += Source.UpdateRate;
        }

        /// <summary>
        /// Close the writer and export movement metadata.
        /// </summary>
        public override void Dispose() {
            if (Output == null) {
                CreateFile(); // Create an empty file if no data was written
            }

            AudioDefinitionModel adm = CreateModel();

            StringBuilder builder = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            if (admWriter != null) {
                settings.Indent = true;
            }
            using (XmlWriter exporter = XmlWriter.Create(builder, settings)) {
                adm.WriteXml(exporter);
            }

            byte[] axml = Encoding.UTF8.GetBytes(builder.ToString().Replace("utf-16", "utf-8"));
            if (admWriter == null) {
                Output.WriteChunk(RIFFWaveConsts.axmlSync, axml, true);
                Output.WriteChunk(RIFFWaveConsts.chnaSync, ChannelAssignment.GetChunk(adm), true);
                WriteAdditionalChunks();
            } else {
                admWriter.Write(axml);
                admWriter.Dispose();
            }
            Output.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Get the length of the environment recording.
        /// </summary>
        public ADMTimeSpan GetContentLength() => new ADMTimeSpan(Output.Length / (double)Output.SampleRate);

        /// <summary>
        /// Generates the ADM structure from the recorded movement.
        /// </summary>
        protected virtual AudioDefinitionModel CreateModel() {
            ADMTimeSpan contentLength = GetContentLength();
            const string channelContentID = "ACO_1001",
                objectContentID = "ACO_1002";
            List<string> channelIDs = new List<string>();
            List<string> objectIDs = new List<string>();

            List<ADMProgramme> programs = new List<ADMProgramme> {
                new ADMProgramme("APR_1001", "Cavern_Export", contentLength) {
                    Contents = new List<string> { channelContentID, objectContentID }
                }
            };
            List<ADMContent> contents = new List<ADMContent> {
                new ADMContent(channelContentID, "Channels") {
                    Objects = channelIDs
                },
                new ADMContent(objectContentID, "Objects") {
                    Objects = objectIDs
                }
            };
            List<ADMObject> objects = new List<ADMObject>();
            List<ADMPackFormat> packFormats = new List<ADMPackFormat>();
            List<ADMChannelFormat> channelFormats = new List<ADMChannelFormat>();
            List<ADMTrack> tracks = new List<ADMTrack>();
            List<ADMTrackFormat> trackFormats = new List<ADMTrackFormat>();
            List<ADMStreamFormat> streamFormats = new List<ADMStreamFormat>();

            int channelIndex = 0, objectIndex = 0;
            for (int i = 0; i < Movements.Length; ++i) {
                bool isDynamic = Movements[i].Count != 1;
                ADMPackType packType = isDynamic ? ADMPackType.Objects : ADMPackType.DirectSpeakers;
                string id = (0x1000 + (isDynamic ? ++objectIndex : ++channelIndex)).ToString("x4"),
                    totalId = (0x1001 + i).ToString("x4"),
                    packHex = ((int)packType).ToString("x4"),
                    objectID = "AO_" + totalId,
                    objectName = isDynamic ? "Cavern_Obj_" + objectIndex :
                        ADMConsts.channelNames[(int)Renderer.ChannelFromPosition(Movements[i][0].Position)],
                    packFormatID = $"AP_{packHex}{id}",
                    channelFormatID = $"AC_{packHex}{id}",
                    trackID = "ATU_0000" + (i + 1).ToString("x4"),
                    trackFormatID = $"AT_{packHex}{id}_01",
                    streamFormatID = $"AS_{packHex}{id}";

                if (isDynamic) {
                    objectIDs.Add(objectID);
                } else {
                    channelIDs.Add(objectID);
                }

                objects.Add(new ADMObject(objectID, isDynamic ? "Audio Object " + objectIndex : objectName,
                    default, contentLength, packFormatID) {
                    Tracks = new List<string> { trackID }
                });
                packFormats.Add(new ADMPackFormat(packFormatID, objectName, packType) {
                    ChannelFormats = new List<string> { channelFormatID }
                });

                FixEndTimings(Movements[i], contentLength);
                channelFormats.Add(new ADMChannelFormat(channelFormatID, objectName, packType) {
                    Blocks = Movements[i]
                });

                tracks.Add(new ADMTrack(trackID, Output.Bits, Output.SampleRate, trackFormatID, packFormatID));
                trackFormats.Add(new ADMTrackFormat(trackFormatID, "PCM_" + objectName, ADMTrackCodec.PCM, streamFormatID));
                streamFormats.Add(new ADMStreamFormat(streamFormatID, "PCM_" + objectName, ADMTrackCodec.PCM,
                    channelFormatID, packFormatID, trackFormatID));
            }

            contents.RemoveAll(content => content.Objects.Count == 0);

            return new AudioDefinitionModel(programs, contents, objects, packFormats, channelFormats,
                tracks, trackFormats, streamFormats) {
                Feedback = FinalFeedback,
                FeedbackStartPercentage = FinalFeedbackStart
            };
        }

        /// <summary>
        /// Additional chunks to write to the BWF file.
        /// </summary>
        protected virtual void WriteAdditionalChunks() { }

        /// <summary>
        /// Makes sure the last block ends with the content.
        /// </summary>
        protected void FixEndTimings(List<ADMBlockFormat> blocks, ADMTimeSpan contentLength) {
            for (int j = 0, c = blocks.Count; j < c; j++) {
                if (blocks.Count != 0) {
                    ADMBlockFormat lastBlock = blocks[^1];
                    if (!lastBlock.Duration.IsZero() && !contentLength.Equals(lastBlock.Offset + lastBlock.Duration)) {
                        ADMTimeSpan newDuration = contentLength - lastBlock.Offset;
                        if (lastBlock.Interpolation.Equals(lastBlock.Duration)) {
                            lastBlock.Interpolation = newDuration;
                        }
                        lastBlock.Duration = newDuration;
                    }
                    ADMBlockFormat fistBlock = blocks[0];
                    if (!fistBlock.Interpolation.IsZero()) {
                        fistBlock.Interpolation = ADMTimeSpan.Zero;
                    }
                }
            }
        }

        /// <summary>
        /// Lazy create the output file. The <see cref="Listener"/> might get initialized after the constructor.
        /// </summary>
        void CreateFile() {
            if (writer is FileStream fs && fs.Name.EndsWith(".xml")) {
                admWriter = writer;
                writer = AudioWriter.Open(fs.Name[..^3] + "wav");
            }

            Output = new RIFFWaveWriter(writer, Source.ActiveSources.Count, Length, Source.SampleRate, bits) {
                MaxLargeChunks = 3
            };
            Output.WriteHeader();

            Movements = new List<ADMBlockFormat>[Output.ChannelCount];
            for (int i = 0; i < Output.ChannelCount; i++) {
                Movements[i] = new List<ADMBlockFormat>();
            }
        }

        /// <summary>
        /// Checks whether an intermediate movement block (<paramref name="prev1"/>) lies within 0.5° geodesic
        /// tolerance along the unit-sphere arc between <paramref name="prev2"/> and <paramref name="currentPos"/>
        /// while bounding total merged arc span to prevent chords through the listener origin.
        /// </summary>
        static bool IsGeodesicDecimatable(
            ADMBlockFormat prev2,
            ADMBlockFormat prev1,
            Vector3 currentPos,
            double newOffsetSeconds) {
            if (prev1.Interpolation.IsZero()) {
                return false;
            }
            float lenSq0 = prev2.Position.LengthSquared();
            float lenSq1 = prev1.Position.LengthSquared();
            float lenSq2 = currentPos.LengthSquared();
            if (lenSq0 < 1e-10f || lenSq1 < 1e-10f || lenSq2 < 1e-10f) {
                return lenSq0 < 1e-10f && lenSq1 < 1e-10f && lenSq2 < 1e-10f;
            }

            float r0 = (float)Math.Sqrt(lenSq0);
            float r1 = (float)Math.Sqrt(lenSq1);
            float r2 = (float)Math.Sqrt(lenSq2);
            Vector3 u0 = prev2.Position / r0;
            Vector3 u1 = prev1.Position / r1;
            Vector3 u2 = currentPos / r2;

            double totalArc = Math.Acos(Math.Max(-1.0, Math.Min(1.0, (double)Vector3.Dot(u0, u2))));
            if (totalArc > MaxMergedArcRad) {
                return false;
            }

            float t = (float)QMath.LerpInverse(prev2.Offset.TotalSeconds, newOffsetSeconds, prev1.Offset.TotalSeconds);
            Vector3 expectedDir = SlerpUnit(u0, u2, t);
            double angleError = Math.Acos(Math.Max(-1.0, Math.Min(1.0, (double)Vector3.Dot(expectedDir, u1))));
            float expectedR = QMath.Lerp(r0, r2, t);
            return angleError <= GeodesicToleranceRad && Math.Abs(expectedR - r1) <= 0.01f;
        }

        /// <summary>
        /// Determines whether the transition from <paramref name="prevPos"/> to <paramref name="currentPos"/>
        /// represents a large discontinuous jump that requires an ADM <c>jumpPosition</c> cut instead of
        /// interpolating a chord through the listener's head.
        /// </summary>
        static bool ShouldJumpCut(Vector3 prevPos, Vector3 currentPos) {
            double angle = GeodesicAngle(prevPos, currentPos, out bool valid);
            if (!valid) {
                return (prevPos - currentPos).LengthSquared() > 0.25f;
            }
            return angle > JumpCutAngleRad;
        }

        static double GeodesicAngle(Vector3 a, Vector3 b, out bool valid) {
            float lenSqA = a.LengthSquared();
            float lenSqB = b.LengthSquared();
            if (lenSqA < 1e-10f || lenSqB < 1e-10f) {
                valid = false;
                return 0.0;
            }
            Vector3 u = a / (float)Math.Sqrt(lenSqA);
            Vector3 v = b / (float)Math.Sqrt(lenSqB);
            double dot = Math.Max(-1.0, Math.Min(1.0, (double)Vector3.Dot(u, v)));
            valid = true;
            return Math.Acos(dot);
        }

        static Vector3 SlerpUnit(Vector3 u, Vector3 v, float t) {
            double dot = Math.Max(-1.0, Math.Min(1.0, (double)Vector3.Dot(u, v)));
            double theta = Math.Acos(dot);
            if (theta < 1e-5) {
                Vector3 blended = Vector3.Lerp(u, v, t);
                float lenSq = blended.LengthSquared();
                return lenSq > 1e-12f ? blended / (float)Math.Sqrt(lenSq) : u;
            }
            double sinTheta = Math.Sin(theta);
            float w0 = (float)(Math.Sin((1.0 - t) * theta) / sinTheta);
            float w1 = (float)(Math.Sin(t * theta) / sinTheta);
            return u * w0 + v * w1;
        }

        /// <summary>
        /// 0.5° unit-sphere geodesic decimation tolerance in radians.
        /// </summary>
        const double GeodesicToleranceRad = 0.5 * Math.PI / 180.0;

        /// <summary>
        /// Maximum spherical arc span (5°) allowed across merged movement blocks so trajectories hug the sphere.
        /// </summary>
        const double MaxMergedArcRad = 5.0 * Math.PI / 180.0;

        /// <summary>
        /// Single-step geodesic angle threshold (60°) above which a clean <c>jumpPosition</c> cut is emitted.
        /// </summary>
        const double JumpCutAngleRad = Math.PI / 3.0;

        /// <summary>
        /// Inverse of <see cref="Vector3.One"/> for clamping.
        /// </summary>
        static readonly Vector3 minusOne = -Vector3.One;
    }
}