using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Cavern.Format.Exceptions;
using Cavern.Format.Utilities;
using Cavern.Utilities;

namespace Cavern.Format.Renderers.CoreAudioFormat {
    /// <summary>
    /// Parses the .metadata file of a Dolby Atmos Master Format export to internal data representations.
    /// </summary>
    internal class DolbyAtmosMasterMetadataFile {
        /// <summary>
        /// Movement data for each dynamic object (bed channel object indices are null).
        /// </summary>
        public MovementTimeframe[][] Movement { get; }

        /// <summary>
        /// Parses the .metadata file of a Dolby Atmos Master Format export to internal data representations.
        /// </summary>
        /// <param name="source">Pre-parsed .metadata file</param>
        /// <param name="mapping">Maps PCM stream indices from the .audio file to internal object ID (those are the values)</param>
        public DolbyAtmosMasterMetadataFile(YAML source, int[] mapping) {
            Dictionary<int, int> inverseMapping = InvertMapping(mapping);
            List<MovementTimeframe>[] movement = new List<MovementTimeframe>[mapping.Length];
            MovementTimeframe[] lastFrames = new MovementTimeframe[mapping.Length];
            for (int i = 0; i < mapping.Length; i++) {
                movement[i] = new List<MovementTimeframe>();
            }

            if (!source.Data.TryGetValue("events", out object rawEvents) ||
                (rawEvents is string rawEvtStr && rawEvtStr == "[]")) {
                Movement = new MovementTimeframe[mapping.Length][];
                return;
            }
            if (!(rawEvents is List<YAMLObject> events)) {
                throw new CorruptionException("No events found in the metadata file.");
            }
            if (events.Count == 0) {
                Movement = new MovementTimeframe[mapping.Length][];
                return;
            }

            int id = -1;
            long offset = 0;
            if (!events[0].ContainsKey("ID")) {
                throw new CorruptionException("First event has no channel ID.");
            }

            for (int i = 0, c = events.Count; i < c; i++) {
                YAMLObject current = events[i];
                string idStr = current.TryGetValue("ID", out object rawID) ? rawID as string : null;
                if (rawID != null && idStr == null) {
                    throw new CorruptionException("Invalid channel ID in the metadata file.");
                }
                string offStr = current.TryGetValue("samplePos", out object rawOffset) ? rawOffset as string : null;
                string posStr = current.TryGetValue("pos", out object rawPosition) ? rawPosition as string : null;
                string gainStr = current.TryGetValue("gain", out object rawGain) ? rawGain as string : null;
                string fadeStr = current.TryGetValue("rampLength", out object rawFade) ? rawFade as string : null;

                ApplyEventFields(idStr, offStr, posStr, gainStr, fadeStr, ref id, ref offset, inverseMapping, movement, lastFrames);
            }

            Movement = FinalizeMovement(movement);
        }

        DolbyAtmosMasterMetadataFile(MovementTimeframe[][] movement) => Movement = movement;

        /// <summary>
        /// Streams a potentially large .metadata YAML file directly from disk without allocating intermediate YAML dictionaries.
        /// </summary>
        public static DolbyAtmosMasterMetadataFile FromFile(string path, int[] mapping) {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using StreamReader reader = new StreamReader(fs);
            Dictionary<int, int> inverseMapping = InvertMapping(mapping);
            List<MovementTimeframe>[] movement = new List<MovementTimeframe>[mapping.Length];
            MovementTimeframe[] lastFrames = new MovementTimeframe[mapping.Length];
            for (int i = 0; i < mapping.Length; i++) {
                movement[i] = new List<MovementTimeframe>();
            }

            bool inEvents = false;
            bool hasCurrent = false;
            bool firstEventSeen = false;
            int id = -1;
            long offset = 0;
            string evId = null, evPos = null, evSamplePos = null, evGain = null, evFade = null;

            void FlushEvent() {
                if (!hasCurrent) {
                    return;
                }
                if (!firstEventSeen) {
                    firstEventSeen = true;
                    if (evId == null) {
                        throw new CorruptionException("First event has no channel ID.");
                    }
                }
                ApplyEventFields(evId, evSamplePos, evPos, evGain, evFade, ref id, ref offset, inverseMapping, movement, lastFrames);
                evId = evPos = evSamplePos = evGain = evFade = null;
                hasCurrent = false;
            }

            string line;
            while ((line = reader.ReadLine()) != null) {
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }
                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') {
                    indent++;
                }
                if (indent == 0) {
                    FlushEvent();
                    if (line.StartsWith("events:", StringComparison.Ordinal)) {
                        string rest = line.Substring(7).Trim();
                        if (rest == "[]") {
                            return new DolbyAtmosMasterMetadataFile(new MovementTimeframe[mapping.Length][]);
                        }
                        inEvents = true;
                        continue;
                    }
                    if (inEvents) {
                        break;
                    }
                    continue;
                }
                if (!inEvents) {
                    continue;
                }

                string trimmed = line.Substring(indent);
                if (trimmed.StartsWith("- ", StringComparison.Ordinal)) {
                    FlushEvent();
                    hasCurrent = true;
                    trimmed = trimmed.Substring(2).TrimStart();
                } else if (!hasCurrent) {
                    continue;
                }

                int colon = trimmed.IndexOf(':');
                if (colon <= 0) {
                    continue;
                }
                string key = trimmed.Substring(0, colon).Trim();
                string val = trimmed.Substring(colon + 1).Trim();
                switch (key) {
                    case "ID": evId = val; break;
                    case "samplePos": evSamplePos = val; break;
                    case "pos": evPos = val; break;
                    case "gain": evGain = val; break;
                    case "rampLength": evFade = val; break;
                }
            }
            FlushEvent();

            if (!inEvents) {
                throw new CorruptionException("No events found in the metadata file.");
            }
            return new DolbyAtmosMasterMetadataFile(FinalizeMovement(movement));
        }

        static void ApplyEventFields(
            string idString, string offsetSource, string positionSource, string gainSource, string fadeSource,
            ref int id, ref long offset,
            Dictionary<int, int> inverseMapping, List<MovementTimeframe>[] movement, MovementTimeframe[] lastFrames) {
            if (idString != null && !int.TryParse(idString, out id)) {
                throw new CorruptionException("Invalid channel ID in the metadata file.");
            }
            if (!inverseMapping.TryGetValue(id, out int mappedId)) {
                return;
            }
            int idx = mappedId;
            if (offsetSource != null) {
                offset = long.Parse(offsetSource);
            }

            Vector3 position = lastFrames[idx].position;
            if (positionSource != null && positionSource.Length >= 2) {
                string[] parts = positionSource[1..^1].Split(", ");
                position = new Vector3(QMath.ParseFloat(parts[0]), QMath.ParseFloat(parts[2]), QMath.ParseFloat(parts[1]));
            }

            float gain = lastFrames[idx].gain;
            if (gainSource != null) {
                gain = gainSource != "-inf" ? QMath.DbToGain(QMath.ParseFloat(gainSource)) : 0;
            }

            int fade = lastFrames[idx].fade;
            if (fadeSource != null) {
                fade = int.Parse(fadeSource);
            }

            lastFrames[idx] = new MovementTimeframe(position, gain, offset, fade);
            movement[idx].Add(lastFrames[idx]);
        }

        static MovementTimeframe[][] FinalizeMovement(List<MovementTimeframe>[] movement) {
            MovementTimeframe[][] result = new MovementTimeframe[movement.Length][];
            for (int i = 0; i < movement.Length; i++) {
                result[i] = movement[i].Count != 0 ? movement[i].ToArray() : null;
            }
            return result;
        }

        /// <summary>
        /// Create a dictionary that maps the internal object IDs to the PCM stream indices of the .audio file.
        /// </summary>
        static Dictionary<int, int> InvertMapping(int[] mapping) {
            Dictionary<int, int> result = new Dictionary<int, int>();
            for (int i = 0; i < mapping.Length; i++) {
                result[mapping[i]] = i;
            }
            return result;
        }
    }
}
