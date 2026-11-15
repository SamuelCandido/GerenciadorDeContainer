using System.Text.Json.Serialization;

namespace MiniDocker.Nucleo.Modelos;

[JsonConverter(typeof(JsonStringEnumConverter<ModoReinicio>))]
public enum ModoReinicio
{
    /// <summary>Nunca reinicia (padrão).</summary>
    Nao,

    /// <summary>Reinicia só se o processo principal terminar com código diferente de zero.</summary>
    EmFalha,

    /// <summary>Reinicia sempre que o processo principal terminar, até um <c>stop</c>.</summary>
    Sempre
}

/// <summary>
/// <c>--restart no | on-failure[:N] | always</c>, como no Docker. Quem aplica é
/// o dono do container: ao ver o processo principal terminar, decide se sobe
/// outro dentro do mesmo Job Object. É o papel de um supervisor de processos —
/// o <c>systemd</c> no Linux, as ações de recuperação de um serviço no Windows.
/// </summary>
public sealed record PoliticaReinicio
{
    public ModoReinicio Modo { get; init; } = ModoReinicio.Nao;

    /// <summary>Tentativas no <c>on-failure:N</c>; <c>null</c> é sem limite.</summary>
    public int? MaximoTentativas { get; init; }

    public static PoliticaReinicio Nunca { get; } = new();

    /// <param name="reiniciosFeitos">Quantos reinícios já houve nesta execução.</param>
    public bool DeveReiniciar(int codigoSaida, int reiniciosFeitos) => Modo switch
    {
        ModoReinicio.Sempre => true,
        ModoReinicio.EmFalha => codigoSaida != 0 && (MaximoTentativas is null || reiniciosFeitos < MaximoTentativas),
        _ => false
    };

    /// <summary>
    /// Espera crescente entre tentativas (1 s, 2 s, 4 s... até 30 s): um
    /// container que falha logo ao subir não vira um laço consumindo CPU.
    /// </summary>
    public static TimeSpan Espera(int reiniciosFeitos) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(reiniciosFeitos, 5))));

    public static PoliticaReinicio Interpretar(string texto)
    {
        var partes = texto.Split(':', 2);
        return partes[0].ToLowerInvariant() switch
        {
            "no" when partes.Length == 1 => Nunca,
            "always" when partes.Length == 1 => new PoliticaReinicio { Modo = ModoReinicio.Sempre },
            "on-failure" => new PoliticaReinicio
            {
                Modo = ModoReinicio.EmFalha,
                MaximoTentativas = partes.Length == 1 ? null
                    : int.TryParse(partes[1], out var n) && n > 0 ? n
                    : throw new ArgumentException($"--restart invalido: '{texto}'. Ex.: on-failure:3")
            },
            _ => throw new ArgumentException($"--restart invalido: '{texto}'. Use no, on-failure[:N] ou always.")
        };
    }

    public override string ToString() => Modo switch
    {
        ModoReinicio.Sempre => "always",
        ModoReinicio.EmFalha => MaximoTentativas is { } n ? $"on-failure:{n}" : "on-failure",
        _ => "no"
    };
}
