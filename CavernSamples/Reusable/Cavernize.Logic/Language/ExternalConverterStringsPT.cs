namespace Cavernize.Logic.Language;

/// <summary>
/// Strings for the messages of external renderers in Portuguese (Brazil).
/// </summary>
public class ExternalConverterStringsPT : ExternalConverterStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "pt-BR";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("LicNe", "O Cavernize usa {0} para conversões {1}. Ele será baixado automaticamente, mas você precisa aceitar a licença antes.");
        Set("LicFe", "Obtendo licença...");
        Set("LicFa", "Falha ao obter a licença do {0}. Provavelmente é um erro de rede.");
        Set("LisWa", "Aguardando aprovação do usuário...");
        Set("LicCa", "A licença não foi aceita.");
        Set("PrgDl", "Baixando {0}...");
        Set("PrgEx", "Extraindo {0}...");
        Set("PrgRB", "Extraindo bitstream bruto...");
        Set("PrgCo", "Convertendo com {0}...");
        Set("ErrNe", "O download falhou devido a um erro de rede.");
    }
}
