using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MiniDocker.Nucleo.Modelos;

/// <summary>
/// Um volume montado no container: uma pasta de fora do rootfs que aparece
/// dentro dele. O que o container grava ali sobrevive ao <c>rm</c>.
/// </summary>
public sealed record Montagem
{
    /// <summary>Caminho absoluto da pasta no host.</summary>
    public required string Origem { get; init; }

    /// <summary>Onde a pasta aparece, relativo ao rootfs (como <c>dados</c>).</summary>
    public required string Destino { get; init; }

    /// <summary>Nome do volume gerenciado pelo MiniDocker; <c>null</c> numa pasta do host.</summary>
    public string? NomeVolume { get; init; }

    /// <summary>
    /// <c>:ro</c>. A pasta é rotulada Medium em vez de Low, então o container
    /// (que roda em Low) lê mas não escreve nela — quem recusa é o kernel.
    /// </summary>
    public bool SomenteLeitura { get; init; }

    [JsonIgnore]
    public bool Nomeado => NomeVolume is not null;
}

/// <summary>Interpreta o <c>-v ORIGEM:DESTINO[:ro]</c> do Docker.</summary>
public static partial class EspecificacaoVolume
{
    /// <summary>
    /// Separa <c>ORIGEM:DESTINO[:ro|:rw]</c>. A origem pode ter letra de unidade
    /// (<c>C:\dados:dados</c>), então o <c>:</c> da posição 1 não separa.
    /// </summary>
    public static (string Origem, string Destino, bool SomenteLeitura) Separar(string especificacao)
    {
        var somenteLeitura = false;
        if (especificacao.EndsWith(":ro", StringComparison.OrdinalIgnoreCase)
            || especificacao.EndsWith(":rw", StringComparison.OrdinalIgnoreCase))
        {
            somenteLeitura = especificacao.EndsWith(":ro", StringComparison.OrdinalIgnoreCase);
            especificacao = especificacao[..^3];
        }

        var inicio = especificacao.Length > 2 && char.IsLetter(especificacao[0]) && especificacao[1] == ':' ? 2 : 0;
        var separador = especificacao.IndexOf(':', inicio);

        if (separador <= 0 || separador == especificacao.Length - 1)
        {
            throw new ArgumentException(
                $"Volume invalido: '{especificacao}'. Use -v PASTA_DO_HOST:DESTINO[:ro] ou -v NOME_DO_VOLUME:DESTINO.");
        }

        var origem = especificacao[..separador];
        var destino = especificacao[(separador + 1)..];

        if (destino.Contains(':'))
        {
            throw new ArgumentException(
                $"Destino de volume invalido: '{destino}'. Use um caminho dentro do container, como 'dados'.");
        }

        return (origem, destino, somenteLeitura);
    }

    /// <summary>
    /// <c>canil</c> é um volume nomeado; <c>C:\dados</c>, <c>.\dados</c> ou
    /// <c>/tmp/x</c> são pastas do host — a mesma regra do Docker.
    /// </summary>
    public static bool EhNomeado(string origem) =>
        !origem.Contains('/') && !origem.Contains('\\') && !origem.Contains(':') && !origem.StartsWith('.');

    public static void ValidarNome(string nome)
    {
        if (!PadraoNome().IsMatch(nome))
        {
            throw new ArgumentException(
                $"Nome de volume invalido: '{nome}'. Use letras, digitos, '_', '.' ou '-'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex PadraoNome();
}
