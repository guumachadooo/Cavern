using System.IO;

using Cavern.Format.Consts;
using Cavern.Format.Decoders;
using Cavern.Format.Exceptions;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.CoreAudioFormat;
using Cavern.Format.Transcoders;
using Cavern.Format.Utilities;

namespace Cavern.Format {
    /// <summary>
    /// Decodes and renders Dolby Atmos Master Format files.
    /// </summary>
    public class DolbyAtmosMasterFormatReader : DecoderBasedAudioReader<UncompressedDecoder, CoreAudioFormatRenderer> {
        /// <summary>
        /// Root object containing channel/object mapping.
        /// </summary>
        readonly YAML root;

        /// <summary>
        /// Path to the object movement metadata YAML file.
        /// </summary>
        readonly string metadataPath;

        /// <summary>
        /// Decodes and renders Dolby Atmos Master Format files.
        /// </summary>
        public DolbyAtmosMasterFormatReader(string path) : base(null, false) {
            root = new YAML(File.ReadAllText(path));
            metadataPath = path + ".metadata";
            if (!File.Exists(metadataPath)) {
                throw new FileNotFoundException("No metadata file (.atmos.metadata) was found.", metadataPath);
            }

            string audioPath = path + ".audio";
            if (File.Exists(audioPath)) {
                reader = OpenSequentialStream(audioPath);
                return;
            }
            if (path.Length > 6) {
                audioPath = path[..^6] + ".caf";
                if (File.Exists(audioPath)) {
                    reader = OpenSequentialStream(audioPath);
                    return;
                }
            }
            throw new FileNotFoundException("No audio file (.atmos.audio or .caf) was found.", path);
        }

        /// <inheritdoc/>
        public override UncompressedDecoder CreateDecoder(bool skipSyncWord) {
            int syncWord = reader.ReadInt32();
            if (syncWord == RIFFWaveConsts.syncWord1 || syncWord == RIFFWaveConsts.syncWord1_64) {
                return new RIFFWaveDecoder(reader, true);
            }
            if (syncWord == CoreAudioFormatConsts.syncWord) {
                return new CoreAudioFormatDecoder(reader, true);
            }
            throw new SyncException();
        }

        /// <inheritdoc/>
        public override CoreAudioFormatRenderer CreateRenderer(UncompressedDecoder decoder) {
            DolbyAtmosMasterFormatRootFile parsedRoot = new DolbyAtmosMasterFormatRootFile(root, decoder.ChannelCount);
            DolbyAtmosMasterMetadataFile metadata = DolbyAtmosMasterMetadataFile.FromFile(metadataPath, parsedRoot.ObjectMapping);
            return new CoreAudioFormatRenderer(decoder, root, metadata);
        }
    }
}
