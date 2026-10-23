using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace MiniDocker.Nucleo.Modelos;

public class Conteiner
{
    public string Id { get; set; } = GerarId();
    public string Nome { get; set; } = string.Empty;
    public string Comando { get; set; } = string.Empty;

    /// <summary>Referência da imagem de onde o rootfs foi copiado.</summary>
    public string Imagem { get; set; } = Modelos.Imagem.NomeScratch;

    /// <summary>Variáveis da imagem somadas às do <c>-e</c>, aplicadas por cima do ambiente mínimo.</summary>
    public Dictionary<string, string> Ambiente { get; set; } = [];
    public EstadoConteiner Estado { get; set; } = EstadoConteiner.Criado;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? IniciadoEm { get; set; }
    public DateTime? FinalizadoEm { get; set; }
    public string CaminhoRootFs { get; set; } = string.Empty;
    public int? IdProcesso { get; set; }
    public int? CodigoSaida { get; set; }

    /// <summary>Limites impostos ao grupo de processos pelo Job Object.</summary>
    public LimitesRecursos Limites { get; set; } = LimitesRecursos.Nenhum;

    /// <summary>Equivalente ao <c>--rm</c> do Docker: o registro some ao terminar.</summary>
    public bool RemoverAoFinalizar { get; set; }

    /// <summary>
    /// Marcado pelo <c>stop</c> antes de encerrar o grupo, para que o processo
    /// dono do container registre <see cref="EstadoConteiner.Parado"/> em vez
    /// de <see cref="EstadoConteiner.Finalizado"/>. Só é limpo no próximo start.
    /// </summary>
    public bool ParadaSolicitada { get; set; }

    /// <summary>Pico de memória do grupo, medido pelo Job Object.</summary>
    public long? MemoriaUsadaBytes { get; set; }
    public double? TempoCpuMs { get; set; }

    /// <summary>Quantos processos o grupo chegou a criar, incluindo os netos.</summary>
    public int? TotalProcessos { get; set; }
    public long? FalhasDePagina { get; set; }

    /// <summary>
    /// Nome do Job Object no namespace da sessão. É por ele que outros processos
    /// do MiniDocker encontram o grupo para parar, inspecionar ou executar nele:
    /// enquanto o nome existir no kernel, o container está vivo.
    /// </summary>
    [JsonIgnore]
    public string NomeTrabalho => $@"Local\MiniDocker-{Id}";

    private static string GerarId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }
}