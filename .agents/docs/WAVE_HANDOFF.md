# Spatial Roadmap Wave Handoff (Compact)
- **Last Completed:** `W10.1 + W11` (`[x]`) — Focusrite Control 2 Full Hardware Parity (Gen 4 **Auto Gain** start/cancel/status poll, per-channel **Clip Safe**, **Air** Off/Presence/Presence+Drive, **Stereo Link Ch 1-2**, **48V**, **Inst/Line**, **Direct Monitor** Off/Mono/Stereo + Mix balance, **Output Mute/Dim**, **Loopback**, **Hardware Readback**, **Save NVRAM**, 4i4 Gen 4) + W11 Slot 2D Auto-Vocal Mix-Match & Live Mastering Vocal Chain (`Cavern/VocalChain/{VocalMixDnaProfile,LiveVocalChainDsp}.cs`, `Cavernize.Logic/Spatial/VocalMixMatchStageProcessor.cs`) + Interactive Web Studio console.
- **Next Wave:** `W12` — **Next-Gen Formats & Studio Batch Exporter (Slot 1 + Studio)**:
  - Add Eclipsa Audio (`IAMF`) decoding, Ambisonics HOA (3rd-order ACN/SN3D), and ADM BWF (`ITU BS.2076`) read/write.
  - Studio offline batch export: Personalized Lossless Binaural (HRTF+ISM+AutoEq), IAMF, HOA B-Format, ADM BWF, Hybrid Neural+DirAC Stereo->7.1.4/Atmos, Spatial Karaoke / Instrumental Atmos Export, Live Karaoke Performance Mixdown, and FLAC+Atmos Hybrid Lossless Spatial Master.
- **Audit First (before coding):** `Cavern.Format` codecs, `CavernizeOutput.cs`, `MergeToContainer.cs`, `StemExtractor.cs`, `MainActivity.ConverterTab.cs`.
- **Invariants:** `Cavern/` & `Cavern.Format/` strict C# 8.0; `targetSdkVersion="28"` immutable; zero fake DSP; native One UI 9 only; 100% EN/PT-BR parity; `EXTRA_REQUESTS.md` pending queue empty.
- **User Command:** When user says `continue`/`continuar`, execute `W12` per `.agents/docs/SPATIAL_ROADMAP.md`.
