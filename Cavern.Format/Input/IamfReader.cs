using System.IO;

using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;

namespace Cavern.Format {
    /// <summary>
    /// Decodes and renders AOMedia IAMF (Eclipsa Audio) OBU files.
    /// </summary>
    public class IamfReader : DecoderBasedAudioReader<IamfDecoder, IamfRenderer>, IMetadataSupplier {
        /// <summary>
        /// Decodes and renders an AOMedia IAMF stream.
        /// </summary>
        public IamfReader(Stream reader) : base(reader, false) { }

        /// <summary>
        /// Decodes and renders an AOMedia IAMF file by path.
        /// </summary>
        public IamfReader(string path) : base(path) { }

        /// <inheritdoc/>
        public override IamfDecoder CreateDecoder(bool skipSyncWord) => new IamfDecoder(reader);

        /// <inheritdoc/>
        public override IamfRenderer CreateRenderer(IamfDecoder decoder) => new IamfRenderer(decoder);

        /// <summary>
        /// Gets the IAMF codec, audio element, and loudness metadata in human-readable format.
        /// </summary>
        public ReadableMetadata GetMetadata() {
            if (Decoder == null) {
                ReadHeader();
            }
            return Decoder?.GetMetadata();
        }
    }
}
