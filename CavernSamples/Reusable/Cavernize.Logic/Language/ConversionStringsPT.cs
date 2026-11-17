namespace Cavernize.Logic.Language;

/// <summary>
/// Strings for the messages of the conversion process in Portuguese (Brazil).
/// </summary>
public class ConversionStringsPT : ConversionStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "pt-BR";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("ErIRo", "O arquivo raiz é inválido. Ele precisa ter uma extensão.");
        Set("ErCFo", "Arquivo de EQ de convolução para o canal {0} não foi encontrado nesta exportação ({1}).");
    }
}
