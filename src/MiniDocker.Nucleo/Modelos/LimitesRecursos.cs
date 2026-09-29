namespace MiniDocker.Nucleo.Modelos;

/// <summary>
/// Limites de recursos aplicados ao container. Diferente das métricas de
/// consumo, que apenas observam, estes valores são impostos pelo sistema
/// operacional: ultrapassá-los faz o processo falhar ou ser encerrado.
/// </summary>
public sealed record LimitesRecursos
{
    /// <summary>Memória máxima por processo, em bytes. Alocar além disso falha.</summary>
    public long? MemoriaMaximaBytes { get; init; }

    /// <summary>Número máximo de processos vivos no grupo. Barra fork bombs.</summary>
    public int? ProcessosMaximos { get; init; }

    /// <summary>Teto de uso de CPU, de 1 a 100 por cento.</summary>
    public int? PercentualMaximoCpu { get; init; }

    /// <summary>Nenhum limite imposto; o grupo ainda é contabilizado e encerrado junto.</summary>
    public static LimitesRecursos Nenhum { get; } = new();

    public bool AlgumDefinido =>
        MemoriaMaximaBytes is not null ||
        ProcessosMaximos is not null ||
        PercentualMaximoCpu is not null;

    public void Validar()
    {
        if (MemoriaMaximaBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoriaMaximaBytes),
                "O limite de memoria precisa ser maior que zero.");
        }

        if (ProcessosMaximos is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessosMaximos),
                "O limite de processos precisa ser maior que zero.");
        }

        if (PercentualMaximoCpu is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(PercentualMaximoCpu),
                "O limite de CPU precisa estar entre 1 e 100 por cento.");
        }
    }
}

/// <summary>Consumo real do grupo, lido do Job Object depois que ele termina.</summary>
public sealed record ContabilidadeTrabalho(
    double TempoCpuMs,
    long PicoMemoriaBytes,
    int TotalProcessos,
    long FalhasDePagina);
