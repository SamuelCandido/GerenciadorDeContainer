using System.Text.RegularExpressions;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Nucleo.Compose;

/// <summary>Um serviço do compose: uma seção <c>[nome]</c> do arquivo.</summary>
public sealed record DefinicaoServico
{
    public required string Nome { get; init; }
    public required string Imagem { get; init; }

    /// <summary>Pasta para construir a imagem antes de subir; <c>null</c> usa uma imagem que já existe.</summary>
    public string? Build { get; init; }

    public string? Comando { get; init; }
    public int Replicas { get; init; } = 1;
    public IReadOnlyList<string> Volumes { get; init; } = [];
    public IReadOnlyList<string> Portas { get; init; } = [];
    public IReadOnlyDictionary<string, string> Ambiente { get; init; } = new Dictionary<string, string>();
    public LimitesRecursos Limites { get; init; } = LimitesRecursos.Nenhum;
    public PoliticaReinicio Reinicio { get; init; } = PoliticaReinicio.Nunca;
    public bool Privilegiado { get; init; }

    /// <summary>Serviços que precisam subir antes deste.</summary>
    public IReadOnlyList<string> DependeDe { get; init; } = [];
}

/// <summary>O arquivo inteiro: o nome do projeto e os serviços, já na ordem de subida.</summary>
public sealed record DefinicaoCompose(string Projeto, string Diretorio, IReadOnlyList<DefinicaoServico> Servicos);

/// <summary>
/// Lê o <c>minicompose.ini</c>: um arquivo INI, de propósito — uma seção por
/// serviço, uma opção por linha, as mesmas opções do <c>run</c>:
///
/// <code>
/// projeto = relatorios
///
/// [processador]
/// build    = processador
/// replicas = 3
/// volume   = ./fila:fila
/// restart  = on-failure:3
///
/// [painel]
/// build   = painel
/// porta   = 8080:80
/// volume  = ./fila:fila:ro
/// depende = processador
/// </code>
///
/// Caminhos relativos (<c>build</c> e pastas de <c>volume</c>) são relativos
/// à pasta do arquivo.
/// </summary>
public static partial class ArquivoCompose
{
    public const string NomePadrao = "minicompose.ini";

    public static DefinicaoCompose Ler(string caminho)
    {
        var completo = Path.GetFullPath(caminho);
        if (!File.Exists(completo))
        {
            throw new FileNotFoundException(
                $"Arquivo '{caminho}' nao encontrado. Crie um {NomePadrao} ou informe com -f.");
        }

        var diretorio = Path.GetDirectoryName(completo)!;
        return Interpretar(File.ReadAllLines(completo), diretorio);
    }

    public static DefinicaoCompose Interpretar(IEnumerable<string> linhas, string diretorio)
    {
        string? projeto = null;
        var secoes = new List<(string Nome, int Linha, List<(string Chave, string Valor, int Linha)> Opcoes)>();
        var numero = 0;

        foreach (var bruta in linhas)
        {
            numero++;
            var linha = bruta.Trim();
            if (linha.Length == 0 || linha[0] is '#' or ';')
            {
                continue;
            }

            if (linha.StartsWith('['))
            {
                if (!linha.EndsWith(']'))
                {
                    throw Erro(numero, $"secao mal fechada: '{linha}'.");
                }

                var nome = linha[1..^1].Trim();
                if (!PadraoNome().IsMatch(nome))
                {
                    throw Erro(numero, $"nome de servico invalido: '{nome}'. Use letras minusculas, digitos, '_' ou '-'.");
                }

                if (secoes.Any(s => s.Nome == nome))
                {
                    throw Erro(numero, $"o servico '{nome}' aparece duas vezes.");
                }

                secoes.Add((nome, numero, []));
                continue;
            }

            var igual = linha.IndexOf('=');
            if (igual <= 0)
            {
                throw Erro(numero, $"esperado 'chave = valor', encontrado '{linha}'.");
            }

            var chave = linha[..igual].Trim().ToLowerInvariant();
            var valor = linha[(igual + 1)..].Trim();

            if (secoes.Count == 0)
            {
                projeto = chave == "projeto"
                    ? NomeValido(valor)
                    : throw Erro(numero, $"'{chave}' fora de um servico. Antes do primeiro [servico] so vale 'projeto'.");
                continue;
            }

            secoes[^1].Opcoes.Add((chave, valor, numero));
        }

        if (secoes.Count == 0)
        {
            throw new FormatException("O compose nao define nenhum servico. Use [nome] para comecar um.");
        }

        // Sem "projeto =", o nome vem da pasta do arquivo, como no docker compose.
        projeto ??= NomeDaPasta(diretorio);

        var servicos = secoes.Select(s => MontarServico(s.Nome, s.Linha, s.Opcoes, diretorio)).ToList();
        return new DefinicaoCompose(projeto, diretorio, OrdenarPorDependencia(servicos));
    }

    private static DefinicaoServico MontarServico(
        string nome, int linhaSecao, List<(string Chave, string Valor, int Linha)> opcoes, string diretorio)
    {
        string? imagem = null, build = null, comando = null;
        var replicas = 1;
        var volumes = new List<string>();
        var portas = new List<string>();
        var ambiente = new Dictionary<string, string>();
        var limites = LimitesRecursos.Nenhum;
        var reinicio = PoliticaReinicio.Nunca;
        var privilegiado = false;
        var depende = new List<string>();

        foreach (var (chave, valor, linha) in opcoes)
        {
            try
            {
                switch (chave)
                {
                    case "imagem": imagem = valor; break;
                    case "build": build = Path.GetFullPath(Path.Combine(diretorio, valor)); break;
                    case "comando": comando = valor; break;
                    case "replicas":
                        replicas = int.TryParse(valor, out var n) && n >= 1 ? n
                            : throw new ArgumentException($"replicas precisa ser um numero maior que zero, nao '{valor}'.");
                        break;
                    case "volume": volumes.Add(ResolverVolume(valor, diretorio)); break;
                    case "porta": MapeamentoPorta.Interpretar(valor); portas.Add(valor); break;
                    case "env":
                        var igual = valor.IndexOf('=');
                        if (igual <= 0)
                        {
                            throw new ArgumentException($"env precisa ser CHAVE=valor, nao '{valor}'.");
                        }

                        ambiente[valor[..igual].Trim()] = valor[(igual + 1)..].Trim();
                        break;
                    case "memoria": limites = limites with { MemoriaMaximaBytes = Unidades.LerBytes(valor) }; break;
                    case "cpus": limites = limites with { PercentualMaximoCpu = Unidades.LerPercentualCpu(valor) }; break;
                    case "pids": limites = limites with { ProcessosMaximos = Unidades.LerInteiro(valor, "pids") }; break;
                    case "restart": reinicio = PoliticaReinicio.Interpretar(valor); break;
                    case "privilegiado": privilegiado = LerSimNao(valor); break;
                    case "depende":
                        depende.AddRange(valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    default:
                        throw new ArgumentException(
                            $"opcao desconhecida '{chave}'. Validas: imagem, build, comando, replicas, volume, porta, env, memoria, cpus, pids, restart, privilegiado, depende.");
                }
            }
            catch (Exception excecao) when (excecao is ArgumentException or FormatException)
            {
                throw Erro(linha, $"[{nome}] {excecao.Message}");
            }
        }

        if (imagem is null && build is null)
        {
            throw Erro(linhaSecao, $"[{nome}] precisa de 'imagem' ou 'build'.");
        }

        try
        {
            ServicoConteiner.ValidarPortasDoGrupo(portas.Select(MapeamentoPorta.Interpretar), replicas);
        }
        catch (ArgumentException excecao)
        {
            throw Erro(linhaSecao, $"[{nome}] {excecao.Message}");
        }

        return new DefinicaoServico
        {
            Nome = nome,
            // Com build e sem imagem, a imagem leva o nome do serviço.
            Imagem = imagem ?? nome,
            Build = build,
            Comando = comando,
            Replicas = replicas,
            Volumes = volumes,
            Portas = portas,
            Ambiente = ambiente,
            Limites = limites,
            Reinicio = reinicio,
            Privilegiado = privilegiado,
            DependeDe = depende
        };
    }

    /// <summary>
    /// Ordem de subida: cada serviço depois dos que ele depende (ordenação
    /// topológica). Um ciclo — a depende de b, b depende de a — é erro.
    /// </summary>
    private static List<DefinicaoServico> OrdenarPorDependencia(List<DefinicaoServico> servicos)
    {
        var porNome = servicos.ToDictionary(s => s.Nome);
        foreach (var servico in servicos)
        {
            if (servico.DependeDe.FirstOrDefault(d => !porNome.ContainsKey(d)) is { } inexistente)
            {
                throw new FormatException($"[{servico.Nome}] depende de '{inexistente}', que nao existe no arquivo.");
            }
        }

        var ordem = new List<DefinicaoServico>();
        var visitando = new HashSet<string>();
        var feitos = new HashSet<string>();

        void Visitar(DefinicaoServico servico)
        {
            if (feitos.Contains(servico.Nome))
            {
                return;
            }

            if (!visitando.Add(servico.Nome))
            {
                throw new FormatException($"Dependencia circular envolvendo '{servico.Nome}'.");
            }

            foreach (var dependencia in servico.DependeDe)
            {
                Visitar(porNome[dependencia]);
            }

            visitando.Remove(servico.Nome);
            feitos.Add(servico.Nome);
            ordem.Add(servico);
        }

        servicos.ForEach(Visitar);
        return ordem;
    }

    /// <summary>Uma pasta do host relativa (<c>./fila</c>) passa a ser relativa ao arquivo, não ao terminal.</summary>
    private static string ResolverVolume(string valor, string diretorio)
    {
        var (origem, destino, somenteLeitura) = EspecificacaoVolume.Separar(valor);
        if (!EspecificacaoVolume.EhNomeado(origem) && !Path.IsPathRooted(origem))
        {
            origem = Path.GetFullPath(Path.Combine(diretorio, origem));
        }

        return $"{origem}:{destino}{(somenteLeitura ? ":ro" : "")}";
    }

    private static bool LerSimNao(string valor) => valor.ToLowerInvariant() switch
    {
        "sim" or "s" or "true" or "yes" or "1" => true,
        "nao" or "não" or "n" or "false" or "no" or "0" => false,
        _ => throw new ArgumentException($"use sim ou nao, nao '{valor}'.")
    };

    private static string NomeDaPasta(string diretorio)
    {
        var nome = Regex.Replace(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(diretorio)).ToLowerInvariant(), "[^a-z0-9_-]", "-").Trim('-');
        return nome.Length == 0 ? "projeto" : nome[..Math.Min(nome.Length, 40)];
    }

    private static string NomeValido(string nome) =>
        PadraoNome().IsMatch(nome) ? nome : throw new FormatException(
            $"Nome de projeto invalido: '{nome}'. Defina 'projeto = nome' no inicio do arquivo.");

    private static FormatException Erro(int linha, string mensagem) => new($"Linha {linha}: {mensagem}");

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,40}$")]
    private static partial Regex PadraoNome();
}
