namespace MiniDocker.Nucleo.Servicos;

/// <summary>Uma linha do MiniDockerfile, como <c>RUN echo ola</c>.</summary>
public sealed record InstrucaoBuild(string Nome, string Argumentos, int Linha)
{
    public override string ToString() => $"{Nome} {Argumentos}";
}

/// <summary>
/// Leitor do MiniDockerfile: um subconjunto do Dockerfile com <c>FROM</c>,
/// <c>COPY</c>, <c>RUN</c>, <c>ENV</c> e <c>CMD</c>. Linhas vazias e
/// comentários (<c>#</c>) são ignorados.
/// </summary>
public static class ArquivoBuild
{
    public const string NomePadrao = "MiniDockerfile";

    private static readonly string[] InstrucoesConhecidas = ["FROM", "COPY", "RUN", "ENV", "CMD"];

    public static IReadOnlyList<InstrucaoBuild> Ler(string caminho)
    {
        return Interpretar(File.ReadAllLines(caminho));
    }

    public static IReadOnlyList<InstrucaoBuild> Interpretar(IEnumerable<string> linhas)
    {
        var instrucoes = new List<InstrucaoBuild>();
        var numero = 0;

        foreach (var bruta in linhas)
        {
            numero++;
            var linha = bruta.Trim();
            if (linha.Length == 0 || linha.StartsWith('#'))
            {
                continue;
            }

            var espaco = linha.IndexOfAny([' ', '\t']);
            var nome = (espaco < 0 ? linha : linha[..espaco]).ToUpperInvariant();
            var argumentos = espaco < 0 ? string.Empty : linha[(espaco + 1)..].Trim();

            if (!InstrucoesConhecidas.Contains(nome))
            {
                throw new FormatException($"Linha {numero}: instrucao desconhecida '{nome}'.");
            }

            if (argumentos.Length == 0)
            {
                throw new FormatException($"Linha {numero}: {nome} precisa de argumentos.");
            }

            instrucoes.Add(new InstrucaoBuild(nome, argumentos, numero));
        }

        if (instrucoes.Count == 0 || instrucoes[0].Nome != "FROM")
        {
            throw new FormatException("O MiniDockerfile precisa comecar com FROM.");
        }

        if (instrucoes.Skip(1).Any(i => i.Nome == "FROM"))
        {
            throw new FormatException("Apenas um FROM e suportado.");
        }

        return instrucoes;
    }

    /// <summary>Aceita <c>ENV CHAVE=valor</c> e <c>ENV CHAVE valor</c>.</summary>
    public static (string Chave, string Valor) SepararEnv(string argumentos)
    {
        var igual = argumentos.IndexOf('=');
        var espaco = argumentos.IndexOfAny([' ', '\t']);
        var separador = igual >= 0 && (espaco < 0 || igual < espaco) ? igual : espaco;

        if (separador <= 0)
        {
            throw new FormatException($"ENV invalido: '{argumentos}'. Use CHAVE=valor.");
        }

        return (argumentos[..separador].Trim(), argumentos[(separador + 1)..].Trim());
    }

    /// <summary>Separa <c>COPY origem destino</c>; o destino é o último termo.</summary>
    public static (string Origem, string Destino) SepararCopy(string argumentos)
    {
        var partes = argumentos.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length != 2)
        {
            throw new FormatException($"COPY invalido: '{argumentos}'. Use COPY origem destino.");
        }

        return (partes[0], partes[1]);
    }
}
