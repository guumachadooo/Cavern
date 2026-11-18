# Ideias e Animações Salvas (`SAVED_IDEAS.md`)

> Este arquivo guarda boas ideias, efeitos visuais, fórmulas e protótipos que surgiram durante o desenvolvimento do CavernizeAndroid (ou futuros projetos) e foram arquivados temporariamente por prioridade ou para manter fidelidade a um padrão específico (como a One UI 9 nativa). Sempre que o usuário perguntar por ideias guardadas ou quiser reaproveitar algum efeito, consulte aqui.

---

## 1. Deformação Elástica / Côncava de Parede nos Sliders (`EvalWallRightX`)
- **Origem:** Ciclo `v2.5.16` em `CavernSamples/CavernizeAndroid/Theme/OneUiTheme.cs` (`OneUiSeekBar`).
- **O que faz:** Quando o preenchimento de um slider em cápsula encosta na parede esquerda (`0 < fillRight < h`), a extremidade direita achata suavemente no centro usando uma superelipse progressiva (`p = 2.0 -> 3.4`), cria uma leve curva côncava central ("barriga ao contrário") enquanto preserva os cantos superior/inferior arredondados, e por fim achata em linha reta vertical ao encostar no mínimo.
- **Motivo do arquivamento:** Na One UI 9 real (`SecRoundedCornerSeekBarDrawable` / `SecBrightnessSliderView`), o recorte na parede esquerda usa apenas `clipPath` da cápsula sobre um retângulo arredondado, sem essa barriga côncava. O usuário pediu para descartar do app principal para ficar idêntico à One UI 9, mas pediu para guardar o efeito (com observação de torná-lo ainda mais sutil — ex.: `dentFactor = -0.08f` em vez de `-0.18f` — caso queira usar futuramente).

### Código C# de Referência
```csharp
/// <summary>
/// Versão original (dent = -0.18f) e sugestão mais sutil (dent = -0.07f..0.09f)
/// para deformação orgânica na colisão de um thumb circular com a parede esquerda.
/// t = Math.Clamp(fillRight / h, 0f, 1f); u in [-1, 1]; r = h * 0.5f.
/// </summary>
static float EvalWallRightX(float t, float u, float r) {
    float au = Math.Clamp(MathF.Abs(u), 0f, 1f);
    if (t >= 0.50f) {
        float s = (t - 0.50f) / 0.50f;
        float p = 2.0f + 1.4f * (1.0f - s);
        float shoulder = r * (0.90f + 0.10f * s);
        float sag = r * MathF.Pow(s, 0.85f);
        float prof = MathF.Sqrt(Math.Max(0f, 1.0f - MathF.Pow(au, p)));
        return shoulder + sag * prof;
    }
    if (t >= 0.12f) {
        float s = (t - 0.12f) / 0.38f;
        float bell = MathF.Sin(MathF.PI * s);
        float shoulder = r * (0.32f + 0.58f * s);
        // Versão ainda mais sutil sugerida: usar ear = 0.03f e dent = -0.08f no lugar de -0.18f
        float ear = 0.06f * r * bell * MathF.Pow(au, 2.2f);
        float dent = -0.18f * r * bell * (1.0f - au * au);
        return shoulder + dent + ear;
    }
    return r * 0.32f * (t / 0.12f);
}
```
