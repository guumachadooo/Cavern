namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used for generating a post-render report in Portuguese (Brazil).
/// </summary>
public class RenderReportStringsPT : RenderReportStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "pt-BR";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("Defau", "Após concluir a renderização, mais informações da faixa aparecerão aqui, como estatísticas reais de uso de objetos.");
        Set("ABeds", "Canais base (bed) realmente presentes");
        Set("AObjs", "Objetos dinâmicos realmente presentes");
        Set("FakeT", "Alvos de renderização não utilizados (vazios)");
        Set("PeaGa", "Nível de pico do quadro de áudio");
        Set("RMSGa", "Nível RMS do conteúdo");
        Set("MacDy", "Macrodinâmica");
        Set("MicDy", "Microdinâmica");
        Set("NoLFE", "O canal LFE estava ausente na origem, sem uso ou não foi renderizado.");
        Set("PeaLF", "Nível de pico do LFE");
        Set("RMSLF", "Nível RMS do LFE");
        Set("MacLF", "Macrodinâmica do LFE");
        Set("MicLF", "Microdinâmica do LFE");
        Set("CheSl", "Nota de impacto (Chest slam)");
        Set("SurUs", "Uso de surround");
        Set("HeiUs", "Uso de altura (teto)");
    }
}
