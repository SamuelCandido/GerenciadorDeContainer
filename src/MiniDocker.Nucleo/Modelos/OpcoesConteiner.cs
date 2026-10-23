namespace MiniDocker.Nucleo.Modelos;

/// <summary>Opções de criação de um container, equivalentes às flags do <c>docker run</c>.</summary>
public sealed record OpcoesConteiner
{
    /// <summary><c>-m</c>, <c>--cpus</c>, <c>--pids-limit</c>.</summary>
    public LimitesRecursos Limites { get; init; } = LimitesRecursos.Nenhum;

    /// <summary><c>-e CHAVE=valor</c>; sobrescreve o <c>ENV</c> da imagem.</summary>
    public IReadOnlyDictionary<string, string> Ambiente { get; init; } =
        new Dictionary<string, string>();

    /// <summary><c>--rm</c>.</summary>
    public bool RemoverAoFinalizar { get; init; }

    public static OpcoesConteiner Padrao { get; } = new();
}
