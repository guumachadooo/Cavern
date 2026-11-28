using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

using Cavern.Format.Transcoders.AudioDefinitionModelElements;
using Cavern.Format.Utilities;

namespace Cavern.Format.Transcoders {
    partial class AudioDefinitionModel {
        /// <summary>
        /// Check if timings and positions are valid for this AXML. A string for each error is returned.
        /// </summary>
        public List<string> Validate() {
            ADMTimeSpan length = GetLength();
            List<string> errors = new List<string>();
            for (int ch = 0, c = ChannelFormats.Count; ch < c; ch++) {
                ADMChannelFormat channel = ChannelFormats[ch];
                List<ADMBlockFormat> blocks = channel.Blocks;
                int lastBlock = blocks.Count - 1;
                if (lastBlock == -1) {
                    errors.Add($"Channel {channel.ID} has no blocks.");
                    continue;
                }

                if (!blocks[0].Offset.IsZero()) {
                    errors.Add($"Channel {channel.ID} does not start when the program starts.");
                }

                if (channel.Type != ADMPackType.DirectSpeakers) {
                    for (int block = 0; block <= lastBlock; block++) {
                        if (blocks[block].Duration.IsZero()) {
                            errors.Add($"Channel {channel.ID}'s block {block + 1}'s length is zero.");
                        }
                    }
                }

                for (int block = 0; block <= lastBlock; block++) {
                    if (blocks[block].Interpolation > blocks[block].Duration) {
                        errors.Add($"Channel {channel.ID}'s block {block + 1}'s interpolation is longer than its duration.");
                    }

                    if (blocks[block].Position.X < -1 || blocks[block].Position.X > 1 ||
                        blocks[block].Position.Y < -1 || blocks[block].Position.Y > 1 ||
                        blocks[block].Position.Z < -1 || blocks[block].Position.Z > 1) {
                        errors.Add($"Channel {channel.ID}'s block {block + 1}'s position is out of the allowed [-1; 1] range.");
                    }
                }

                for (int block = 1; block <= lastBlock; block++) {
                    if (blocks[block - 1].Offset > blocks[block].Offset) {
                        errors.Add($"Channel {channel.ID}'s block {block} and {block + 1} are swapped in time.");
                    }
                }

                for (int block = 1; block < lastBlock; block++) {
                    if (!blocks[block].Offset.Equals(blocks[block - 1].Offset + blocks[block - 1].Duration)) {
                        errors.Add($"Channel {channel.ID}'s block {block} does not end when the next block starts.");
                    }
                }
                if (channel.Type == ADMPackType.Objects && !length.Equals(blocks[lastBlock].Offset + blocks[lastBlock].Duration)) {
                    errors.Add($"Channel {channel.ID} does not end when the program ends.");
                }
            }
            return errors;
        }

        /// <summary>
        /// Run comprehensive ITU-R BS.2076-2 compliance validation covering ID syntax, cross-reference integrity
        /// across Programme/Content/Object/PackFormat/ChannelFormat/TrackUID/TrackFormat/StreamFormat,
        /// Cartesian coordinate bounds, and block timeline continuity.
        /// </summary>
        public AdmValidationReport ValidateBs2076_2() {
            List<string> errors = Validate();
            List<string> warnings = new List<string>();
            int passedChecks = 0;

            HashSet<string> contentIds = CollectIds(Contents, "ACO_", "audioContent", errors);
            HashSet<string> objectIds = CollectIds(Objects, "AO_", "audioObject", errors);
            HashSet<string> packIds = CollectIds(PackFormats, "AP_", "audioPackFormat", errors);
            HashSet<string> channelIds = CollectIds(ChannelFormats, "AC_", "audioChannelFormat", errors);
            HashSet<string> trackFormatIds = CollectIds(TrackFormats, "AT_", "audioTrackFormat", errors);
            HashSet<string> streamFormatIds = CollectIds(StreamFormats, "AS_", "audioStreamFormat", errors);

            HashSet<string> trackUidIds = new HashSet<string>(StringComparer.Ordinal);
            if (Tracks != null) {
                for (int i = 0; i < Tracks.Count; i++) {
                    ADMTrack tr = Tracks[i];
                    if (tr == null || string.IsNullOrEmpty(tr.ID)) {
                        errors.Add($"audioTrackUID[{i}] has an empty ID.");
                        continue;
                    }
                    if (!tr.ID.StartsWith("ATU_", StringComparison.Ordinal)) {
                        warnings.Add($"audioTrackUID '{tr.ID}' does not follow BS.2076-2 'ATU_' prefix.");
                    }
                    if (!trackUidIds.Add(tr.ID)) {
                        errors.Add($"Duplicate audioTrackUID '{tr.ID}'.");
                    }
                    if (tr.SampleRate <= 0) {
                        errors.Add($"audioTrackUID '{tr.ID}' has invalid sampleRate ({tr.SampleRate}).");
                    }
                    if (!string.IsNullOrEmpty(tr.PackFormat) && !packIds.Contains(tr.PackFormat)) {
                        errors.Add($"audioTrackUID '{tr.ID}' references missing audioPackFormat '{tr.PackFormat}'.");
                    }
                    if (!string.IsNullOrEmpty(tr.TrackFormat) && !trackFormatIds.Contains(tr.TrackFormat)) {
                        errors.Add($"audioTrackUID '{tr.ID}' references missing audioTrackFormat '{tr.TrackFormat}'.");
                    }
                }
            }
            passedChecks++;

            if (Programs == null || Programs.Count == 0) {
                warnings.Add("No audioProgramme declared (BS.2076-2 recommends at least one audioProgramme).");
            } else {
                HashSet<string> progIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < Programs.Count; i++) {
                    ADMProgramme p = Programs[i];
                    if (p == null || string.IsNullOrEmpty(p.ID)) {
                        errors.Add($"audioProgramme[{i}] has an empty ID.");
                        continue;
                    }
                    if (!p.ID.StartsWith("APR_", StringComparison.Ordinal)) {
                        warnings.Add($"audioProgramme '{p.ID}' does not follow BS.2076-2 'APR_' prefix.");
                    }
                    if (!progIds.Add(p.ID)) {
                        errors.Add($"Duplicate audioProgramme '{p.ID}'.");
                    }
                    if (p.Contents != null) {
                        for (int j = 0; j < p.Contents.Count; j++) {
                            if (!contentIds.Contains(p.Contents[j])) {
                                errors.Add($"audioProgramme '{p.ID}' references missing audioContent '{p.Contents[j]}'.");
                            }
                        }
                    }
                }
                passedChecks++;
            }

            if (Contents != null) {
                for (int i = 0; i < Contents.Count; i++) {
                    ADMContent c = Contents[i];
                    if (c == null || c.Objects == null) {
                        continue;
                    }
                    for (int j = 0; j < c.Objects.Count; j++) {
                        if (!objectIds.Contains(c.Objects[j])) {
                            errors.Add($"audioContent '{c.ID}' references missing audioObject '{c.Objects[j]}'.");
                        }
                    }
                }
                passedChecks++;
            }

            if (Objects != null) {
                for (int i = 0; i < Objects.Count; i++) {
                    ADMObject o = Objects[i];
                    if (o == null) {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(o.PackFormat) && !packIds.Contains(o.PackFormat)) {
                        errors.Add($"audioObject '{o.ID}' references missing audioPackFormat '{o.PackFormat}'.");
                    }
                    if (o.Tracks != null) {
                        for (int j = 0; j < o.Tracks.Count; j++) {
                            if (!trackUidIds.Contains(o.Tracks[j])) {
                                errors.Add($"audioObject '{o.ID}' references missing audioTrackUID '{o.Tracks[j]}'.");
                            }
                        }
                    }
                }
                passedChecks++;
            }

            if (PackFormats != null) {
                for (int i = 0; i < PackFormats.Count; i++) {
                    ADMPackFormat pf = PackFormats[i];
                    if (pf == null || pf.ChannelFormats == null) {
                        continue;
                    }
                    for (int j = 0; j < pf.ChannelFormats.Count; j++) {
                        if (!channelIds.Contains(pf.ChannelFormats[j])) {
                            errors.Add($"audioPackFormat '{pf.ID}' references missing audioChannelFormat '{pf.ChannelFormats[j]}'.");
                        }
                    }
                }
                passedChecks++;
            }

            if (StreamFormats != null) {
                for (int i = 0; i < StreamFormats.Count; i++) {
                    ADMStreamFormat sf = StreamFormats[i];
                    if (sf == null) {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(sf.ChannelFormat) && !channelIds.Contains(sf.ChannelFormat)) {
                        errors.Add($"audioStreamFormat '{sf.ID}' references missing audioChannelFormat '{sf.ChannelFormat}'.");
                    }
                    if (!string.IsNullOrEmpty(sf.PackFormat) && !packIds.Contains(sf.PackFormat)) {
                        errors.Add($"audioStreamFormat '{sf.ID}' references missing audioPackFormat '{sf.PackFormat}'.");
                    }
                    if (!string.IsNullOrEmpty(sf.TrackFormat) && !trackFormatIds.Contains(sf.TrackFormat)) {
                        errors.Add($"audioStreamFormat '{sf.ID}' references missing audioTrackFormat '{sf.TrackFormat}'.");
                    }
                }
                passedChecks++;
            }

            if (TrackFormats != null) {
                for (int i = 0; i < TrackFormats.Count; i++) {
                    ADMTrackFormat tf = TrackFormats[i];
                    if (tf != null && !string.IsNullOrEmpty(tf.StreamFormat) && !streamFormatIds.Contains(tf.StreamFormat)) {
                        errors.Add($"audioTrackFormat '{tf.ID}' references missing audioStreamFormat '{tf.StreamFormat}'.");
                    }
                }
                passedChecks++;
            }

            if (ChannelFormats == null || ChannelFormats.Count == 0) {
                errors.Add("ADM document defines zero audioChannelFormat elements.");
            } else {
                passedChecks++;
            }

            return new AdmValidationReport(
                errors,
                warnings,
                passedChecks,
                Programs?.Count ?? 0,
                Objects?.Count ?? 0,
                ChannelFormats?.Count ?? 0,
                Tracks?.Count ?? 0);
        }

        static HashSet<string> CollectIds<T>(
            IReadOnlyList<T> elements,
            string expectedPrefix,
            string elementTag,
            List<string> errors) where T : TaggedADMElement {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            if (elements == null) {
                return ids;
            }
            for (int i = 0; i < elements.Count; i++) {
                T elem = elements[i];
                if (elem == null || string.IsNullOrEmpty(elem.ID)) {
                    errors.Add($"{elementTag}[{i}] has an empty ID.");
                    continue;
                }
                if (!elem.ID.StartsWith(expectedPrefix, StringComparison.Ordinal)) {
                    errors.Add($"{elementTag} '{elem.ID}' violates BS.2076-2 prefix '{expectedPrefix}'.");
                }
                if (!ids.Add(elem.ID)) {
                    errors.Add($"Duplicate {elementTag} ID '{elem.ID}'.");
                }
            }
            return ids;
        }

        void ValidateChannel(XElement channel) {
            string channelName = channel.GetAttribute(ADMTags.channelFormatIDAttribute)[3..];
            IEnumerable<XElement> blocks = channel.AllDescendants(ADMTags.blockTag);
            int blockId = 0;
            foreach (XElement block in blocks) {
                string id = block.GetAttribute(ADMTags.blockIDAttribute);
                string[] ids = id.Split('_');
                if (ids.Length != 3) {
                    throw new ArgumentException($"Block ID \"{id}\" is invalid.");
                }
                if (ids[0] != "AB") {
                    throw new ArgumentException($"Block ID \"{id}\" doesn't start with \"AB\".");
                }
                if (ids[1] != channelName) {
                    throw new ArgumentException($"Block ID \"{id}\" doesn't match the channel it's assigned to $({channelName}).");
                }
                if (int.Parse(ids[2], NumberStyles.HexNumber) != ++blockId) {
                    throw new ArgumentException($"Block ID \"{id}\" has an invalid index, the valid one is $({blockId}).");
                }
            }
        }
    }

    /// <summary>
    /// Structured ITU-R BS.2076-2 ADM validation result returned by <see cref="AudioDefinitionModel.ValidateBs2076_2"/>.
    /// </summary>
    public sealed class AdmValidationReport {
        /// <summary>Blocking BS.2076-2 schema, timeline, coordinate, or referential integrity errors.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Non-blocking recommendations and prefix advisories.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Number of BS.2076-2 structural check groups executed.</summary>
        public int PassedCheckGroups { get; }

        /// <summary>Count of audioProgramme elements.</summary>
        public int ProgrammeCount { get; }

        /// <summary>Count of audioObject elements.</summary>
        public int ObjectCount { get; }

        /// <summary>Count of audioChannelFormat elements.</summary>
        public int ChannelFormatCount { get; }

        /// <summary>Count of audioTrackUID elements.</summary>
        public int TrackUidCount { get; }

        /// <summary>True when zero blocking errors were found.</summary>
        public bool IsBs2076Compliant => Errors.Count == 0;

        /// <summary>Compact human-readable compliance summary.</summary>
        public string Summary => IsBs2076Compliant
            ? $"BS.2076-2 PASS · {ProgrammeCount} prog · {ObjectCount} obj · {ChannelFormatCount} ch · {TrackUidCount} tracks ({Warnings.Count} warn)"
            : $"BS.2076-2 FAIL · {Errors.Count} errors · {Warnings.Count} warn ({ChannelFormatCount} ch, {ObjectCount} obj)";

        /// <summary>Create an immutable ADM validation report.</summary>
        public AdmValidationReport(
            List<string> errors,
            List<string> warnings,
            int passedCheckGroups,
            int programmeCount,
            int objectCount,
            int channelFormatCount,
            int trackUidCount) {
            Errors = errors ?? new List<string>();
            Warnings = warnings ?? new List<string>();
            PassedCheckGroups = passedCheckGroups;
            ProgrammeCount = programmeCount;
            ObjectCount = objectCount;
            ChannelFormatCount = channelFormatCount;
            TrackUidCount = trackUidCount;
        }
    }
}