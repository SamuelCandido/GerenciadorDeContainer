using System.Globalization;

namespace MiniDocker.Nucleo.Modelos;

/// <summary>Os formatos do Docker para memória e CPU, usados pela CLI e pelo compose.</summary>
public static class Unidades
{
    /// <summary>Aceita <c>512k</c>, <c>256m</c>, <c>1g</c> (ou com <c>b</c> no fim).</summary>
    public static long LerBytes(string texto)
    {
        var valor = texto.Trim().ToLowerInvariant().TrimEnd('b');
        long multiplicador = 1;
        if (valor.Length > 0 && valor[^1] is 'k' or 'm' or 'g')
        {
            multiplicador = valor[^1] switch { 'k' => 1L << 10, 'm' => 1L << 20, _ => 1L << 30 };
            valor = valor[..^1];
        }

        if (!long.TryParse(valor, out var numero) || numero <= 0)
        {
            throw new ArgumentException($"Memoria invalida: '{texto}'. Exemplos: 256m, 1g.");
        }

        return numero * multiplicador;
    }

    /// <summary>
    /// O Docker mede CPU em núcleos (<c>0.5</c> = meio núcleo); o Job Object, em
    /// percentual da máquina inteira. A conversão divide pelo número de núcleos.
    /// </summary>
    public static int LerPercentualCpu(string texto)
    {
        if (!double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus) || cpus <= 0)
        {
            throw new ArgumentException($"--cpus invalido: '{texto}'. Exemplo: 0.5");
        }

        var nucleos = Environment.ProcessorCount;
        if (cpus > nucleos)
        {
            throw new ArgumentException($"--cpus {texto} excede os {nucleos} nucleos desta maquina.");
        }

        return Math.Clamp((int)Math.Ceiling(cpus / nucleos * 100), 1, 100);
    }

    public static int LerInteiro(string texto, string opcao) =>
        int.TryParse(texto, out var numero) ? numero : throw new ArgumentException($"{opcao} invalido: '{texto}'.");
}
