using System.Security.Cryptography;

namespace MiniDocker.Nucleo.Modelos;

public class Conteiner
{
    public string Id { get; set; } = GerarId();
    public string Nome { get; set; } = string.Empty;
    public string Comando { get; set; } = string.Empty;
    public EstadoConteiner Estado { get; set; } = EstadoConteiner.Criado;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? IniciadoEm { get; set; }
    public DateTime? FinalizadoEm { get; set; }
    public string CaminhoRootFs { get; set; } = string.Empty;
    public int? IdProcesso { get; set; }
    public int? CodigoSaida { get; set; }

    /// <summary>Limites impostos ao grupo de processos pelo Job Object.</summary>
    public LimitesRecursos Limites { get; set; } = LimitesRecursos.Nenhum;

    /// <summary>Pico de memória do grupo, medido pelo Job Object.</summary>
    public long? MemoriaUsadaBytes { get; set; }
    public double? TempoCpuMs { get; set; }

    /// <summary>Quantos processos o grupo chegou a criar, incluindo os netos.</summary>
    public int? TotalProcessos { get; set; }
    public long? FalhasDePagina { get; set; }

    private static string GerarId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }
}