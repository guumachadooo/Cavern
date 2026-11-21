namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used in the track selection UI in Portuguese (Brazil).
/// </summary>
public class TrackStringsPT : TrackStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "pt-BR";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("NoSup", "Formato não suportado pelo Cavern");
        Set("E3JOC", "Enhanced AC-3 com Joint Object Coding (Dolby Atmos)");
        Set("ObTra", "Faixa de áudio baseada em objetos");
        Set("ChTra", "Faixa de áudio baseada em canais");
        Set("SouCh", "Canais de origem");
        Set("MatBe", "Canais base na matriz");
        Set("MatOb", "Objetos na matriz");
        Set("SouBe", "Canais base (bed)");
        Set("SouDy", "Objetos dinâmicos 3D");
        Set("Chans", "Canais");
        Set("WiObj", "com objetos");
        Set("InvTr", "Não foi possível decodificar esta faixa. O seguinte erro ocorreu durante a decodificação:");
        Set("Later", "Isso pode ser corrigido em versões futuras. Atualize o Cavernize ou informe o problema caso ele persista.");
        Set("PCMFl", "PCM (ponto flutuante)");
        Set("PCMLE", "PCM (inteiro)");
        Set("C_AC3", "AC-3 (médio, suporta SPDIF)");
        Set("CEAC3", "Enhanced AC-3 (médio, suporta HDMI ARC)");
        Set("COpus", "Opus (transparente, tamanho pequeno)");
        Set("CFLAC", "FLAC (sem perdas, tamanho grande)");
        Set("CPCMF", "PCM float (desnecessário, maior tamanho)");
        Set("CPCMI", "PCM inteiro (sem perdas, tamanho grande)");
        Set("CADMC", "ADM Broadcast Wave Format (compacto)");
        Set("CADMA", "ADM Broadcast Wave Format (Dolby Atmos)");
        Set("CIAMF", "Eclipsa Audio — IAMF v1.0 (OBU 3D)");
        Set("CHOA3", "Ambisonics HOA B-Format (3ª Ordem ACN/SN3D)");
        Set("CDAMF", "Dolby Atmos Master Format");
        Set("C_LAF", "Limitless Audio Format");
    }
}
