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

    /// <summary><c>-v ORIGEM:DESTINO</c>, repetível. A origem é uma pasta do host ou o nome de um volume.</summary>
    public IReadOnlyList<string> Volumes { get; init; } = [];

    /// <summary><c>--privileged</c>: desliga o confinamento do sistema de arquivos.</summary>
    public bool Privilegiado { get; init; }

    /// <summary><c>--restart</c>.</summary>
    public PoliticaReinicio Reinicio { get; init; } = PoliticaReinicio.Nunca;

    /// <summary>Grupo de réplicas ao qual o container pertence.</summary>
    public string? Grupo { get; init; }

    /// <summary>Projeto do compose.</summary>
    public string? Projeto { get; init; }

    /// <summary><c>-p [IP:]PORTA_HOST:PORTA_CONTAINER</c>, repetível.</summary>
    public IReadOnlyList<string> Portas { get; init; } = [];

    public static OpcoesConteiner Padrao { get; } = new();
}
