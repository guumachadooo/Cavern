namespace CavernizeAndroid.Language;

public sealed partial class AndroidStrings {
    static void ApplyHungarian(Dictionary<string, string> d) {
        d["OpenFile"] = "Fájl megnyitása";
        d["TrackLabel"] = "Sáv:";
        d["TabMusicPlayer"] = "Lejátszó";
        d["TabPlayer"] = "3D Szoba";
        d["TabConvert"] = "Konvertálás";
        d["TabExtract"] = "Kivonó";
        d["TabSettings"] = "Beállítások";
        d["ModeCover"] = "Borító";
        d["ModeVideo"] = "Videó";
        d["ModeLyrics"] = "Dalszöveg";
        d["Play"] = "Lejátszás";
        d["Pause"] = "Szünet";
        d["Stop"] = "Leállítás";
        d["RenderBtn"] = "Renderelés";
        d["AddQueueBtn"] = "Sorhoz adás";
        d["ShowMetaBtn"] = "Kodek metaadatok";
        d["ShowReportBtn"] = "Renderelési jelentés";
        d["WrGui"] = "Bekötési útmutató";
        d["ErrorTitle"] = "Hiba";
        d["UpdateCardTitle"] = "Szoftverfrissítés";
        d["CheckUpdateBtn"] = "Frissítések keresése";
        d["DownloadInstallBtn"] = "Letöltés és telepítés";
        d["LockRoom"] = "Hallgató mozgatása";
        d["UnlockRoom"] = "Pozíció zárolva";
        d["FineTuneBtn"] = "Finomhangolás (csúszkák)";
        d["FineTuneHideBtn"] = "Csúszkák elrejtése";

        // Wave 2B Studio tömörítés, háttérexport és 3D objektum-koreográfia
        d["StudioCompressionTierLabel"] = "Tömörítés és minőség";
        d["TipStudioCompressionTier"] = "Válassza ki az exportált térhangzású mester csomagolását:\n\n• Master PCM: tömörítetlen stúdió PCM WAV, IAMF vagy AMB.\n• Veszteségmentes: kisebb, bitpontos FLAC / tömörített fájl (nem igényel LGSR párt).\n• Hordozható: kompakt Opus / E-AC-3 mobil megosztáshoz.";
        d["StudioTierMasterPcm"] = "Master PCM";
        d["StudioTierLossless"] = "Veszteségmentes";
        d["StudioTierPortable"] = "Hordozható";
        d["StudioCodecDetailsToggleFmt"] = "Kodek részletek · {0} ({1})";
        d["StudioCodecDetailsBodyFmt"] = "Konténer: {0} · Spec: {1} · Plafon: {2:0.0} dBTP";
        d["StudioAtmosTierLockWarn"] = "A Dolby Atmos ADM BWF a dinamikus 3D objektumok metaadatainak megőrzéséhez Master PCM-et igényel. Váltson Eclipsa IAMF-re a tömörítéshez.";
        d["StudioSwitchToEclipsaBtn"] = "Váltás Eclipsa IAMF-re";
        d["StudioExportLowSpaceError"] = "Nincs elég szabad tárhely ehhez az exportáláshoz.";
        d["StudioExportFgCancelledToast"] = "Exportálás megszakítva.";
        d["SpatialChoreoLabel"] = "3D objektum koreográfia:";
        d["TipSpatialChoreo"] = "Finoman mozgatja a szeparált 3D hangsávokat a szobában hanganalízis és dalszöveg-hangulat alapján (csak 3D pozíciók — vokál FX nélkül).\n\n• Ki: statikus ITU-R BS.2127 pozíciók.\n• Finom: enyhe elülső/magassági lélegzés.\n• Zenei: ütemhez és részekhez igazított 3D tér.\n• Mozis: széles surround és magassági ívek.\n• Keringés: folyamatos gömbölyű keringés.";
        d["SpatialChoreoOff"] = "Ki (Statikus)";
        d["SpatialChoreoSubtle"] = "Finom";
        d["SpatialChoreoMusical"] = "Zenei";
        d["SpatialChoreoCinematic"] = "Mozis";
        d["SpatialChoreoOrbit"] = "Keringés";
    }
}
