using System.IO.Compression;
using System.Text.Json;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Repositorios;

namespace MiniDocker.Nucleo.Servicos;

public class ServicoImagem : IServicoImagem
{
    private const string EntradaMetadados = "imagem.json";
    private const string PastaRootFs = "rootfs";

    private readonly IRepositorioImagem repositorio;
    private readonly string raizImagens;

    public ServicoImagem(string? raizWorkspace = null)
        : this(new RepositorioImagemArquivo(raizWorkspace), raizWorkspace)
    {
    }

    public ServicoImagem(IRepositorioImagem repositorio, string? raizWorkspace = null)
    {
        this.repositorio = repositorio;
        raizImagens = Path.Combine(Workspace.Raiz(raizWorkspace), "images");
        Directory.CreateDirectory(raizImagens);
    }

    public Imagem Construir(string contexto, string referencia, string? arquivo = null, Action<string>? saida = null)
    {
        var (nomeRepositorio, tag) = ReferenciaImagem.Separar(referencia);
        contexto = Path.GetFullPath(contexto);
        if (!Directory.Exists(contexto))
        {
            throw new DirectoryNotFoundException($"Contexto de build '{contexto}' nao existe.");
        }

        var instrucoes = ArquivoBuild.Ler(arquivo ?? Path.Combine(contexto, ArquivoBuild.NomePadrao));
        saida ??= _ => { };

        var imagem = new Imagem { Repositorio = nomeRepositorio, Tag = tag };
        var diretorioTemporario = Path.Combine(raizImagens, $".build-{Guid.NewGuid():N}");
        var rootfs = Path.Combine(diretorioTemporario, PastaRootFs);
        Directory.CreateDirectory(rootfs);

        try
        {
            for (var passo = 0; passo < instrucoes.Count; passo++)
            {
                var instrucao = instrucoes[passo];
                saida($"Passo {passo + 1}/{instrucoes.Count} : {instrucao}");
                AplicarInstrucao(instrucao, imagem, contexto, rootfs, saida);
            }

            return Registrar(imagem, diretorioTemporario);
        }
        catch
        {
            Workspace.ApagarDiretorio(diretorioTemporario);
            throw;
        }
    }

    public IReadOnlyList<Imagem> Listar() =>
        repositorio.ObterTodas().OrderByDescending(i => i.CriadaEm).ToList();

    public Imagem Obter(string referenciaOuId)
    {
        if (referenciaOuId is Imagem.NomeScratch or $"{Imagem.NomeScratch}:latest")
        {
            return Imagem.Scratch;
        }

        if (TentarNormalizar(referenciaOuId) is { } referencia
            && repositorio.ObterPorReferencia(referencia) is { } porReferencia)
        {
            return porReferencia;
        }

        var porId = repositorio.ObterTodas()
            .Where(i => i.Id.StartsWith(referenciaOuId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return porId.Count switch
        {
            1 => porId[0],
            0 => throw new InvalidOperationException(
                $"Imagem '{referenciaOuId}' nao existe. Use 'minidocker build' ou 'minidocker load'."),
            _ => throw new InvalidOperationException(
                $"O prefixo '{referenciaOuId}' corresponde a mais de uma imagem.")
        };
    }

    public void Remover(string referenciaOuId)
    {
        var imagem = Obter(referenciaOuId);
        if (imagem.EhScratch)
        {
            throw new InvalidOperationException("A imagem scratch e embutida e nao pode ser removida.");
        }

        repositorio.Remover(imagem.Id);
        Workspace.ApagarDiretorio(Path.Combine(raizImagens, imagem.Id));
    }

    public Imagem Commit(Conteiner conteiner, string referencia)
    {
        var (nomeRepositorio, tag) = ReferenciaImagem.Separar(referencia);
        var origem = conteiner.Imagem == Imagem.NomeScratch ? null : TentarObter(conteiner.Imagem);

        var imagem = new Imagem
        {
            Repositorio = nomeRepositorio,
            Tag = tag,
            Comando = conteiner.Comando,
            Ambiente = new Dictionary<string, string>(conteiner.Ambiente),
            Historico = [.. origem?.Historico ?? [], $"COMMIT do container {conteiner.Nome}"]
        };

        var diretorioTemporario = Path.Combine(raizImagens, $".commit-{Guid.NewGuid():N}");
        try
        {
            Workspace.CopiarDiretorio(conteiner.CaminhoRootFs, Path.Combine(diretorioTemporario, PastaRootFs));
            return Registrar(imagem, diretorioTemporario);
        }
        catch
        {
            Workspace.ApagarDiretorio(diretorioTemporario);
            throw;
        }
    }

    public void Exportar(string referenciaOuId, string arquivoZip)
    {
        var imagem = Obter(referenciaOuId);
        if (imagem.EhScratch)
        {
            throw new InvalidOperationException("A imagem scratch e embutida e nao precisa ser exportada.");
        }

        if (File.Exists(arquivoZip))
        {
            File.Delete(arquivoZip);
        }

        using var zip = ZipFile.Open(arquivoZip, ZipArchiveMode.Create);
        var metadados = zip.CreateEntry(EntradaMetadados);
        using (var fluxo = metadados.Open())
        {
            JsonSerializer.Serialize(fluxo, imagem, new JsonSerializerOptions { WriteIndented = true });
        }

        // Entradas de diretório preservam pastas vazias, que o zip não guardaria.
        foreach (var diretorio in Directory.EnumerateDirectories(imagem.CaminhoRootFs, "*", SearchOption.AllDirectories))
        {
            zip.CreateEntry(NomeEntrada(imagem.CaminhoRootFs, diretorio) + "/");
        }

        foreach (var arquivo in Directory.EnumerateFiles(imagem.CaminhoRootFs, "*", SearchOption.AllDirectories))
        {
            zip.CreateEntryFromFile(arquivo, NomeEntrada(imagem.CaminhoRootFs, arquivo));
        }
    }

    public Imagem Importar(string arquivoZip)
    {
        var diretorioTemporario = Path.Combine(raizImagens, $".load-{Guid.NewGuid():N}");
        try
        {
            // ExtractToDirectory recusa entradas que tentem sair do destino
            // (zip slip), então um zip malicioso não escreve fora do workspace.
            ZipFile.ExtractToDirectory(arquivoZip, diretorioTemporario);

            var caminhoMetadados = Path.Combine(diretorioTemporario, EntradaMetadados);
            if (!File.Exists(caminhoMetadados))
            {
                throw new InvalidOperationException($"'{arquivoZip}' nao e uma imagem do MiniDocker.");
            }

            var imagem = JsonSerializer.Deserialize<Imagem>(File.ReadAllText(caminhoMetadados))
                ?? throw new InvalidOperationException("Metadados da imagem vazios.");
            File.Delete(caminhoMetadados);
            ReferenciaImagem.Separar(imagem.Referencia);
            Directory.CreateDirectory(Path.Combine(diretorioTemporario, PastaRootFs));

            return Registrar(imagem, diretorioTemporario);
        }
        catch
        {
            Workspace.ApagarDiretorio(diretorioTemporario);
            throw;
        }
    }

    private void AplicarInstrucao(
        InstrucaoBuild instrucao, Imagem imagem, string contexto, string rootfs, Action<string> saida)
    {
        switch (instrucao.Nome)
        {
            case "FROM":
                var baseImagem = Obter(instrucao.Argumentos);
                if (!baseImagem.EhScratch)
                {
                    Workspace.CopiarDiretorio(baseImagem.CaminhoRootFs, rootfs);
                    imagem.Comando = baseImagem.Comando;
                    imagem.Ambiente = new Dictionary<string, string>(baseImagem.Ambiente);
                    imagem.Historico.AddRange(baseImagem.Historico);
                }
                break;

            case "COPY":
                var (origemRelativa, destinoRelativo) = ArquivoBuild.SepararCopy(instrucao.Argumentos);
                Copiar(
                    Workspace.CaminhoDentro(contexto, origemRelativa),
                    Workspace.CaminhoDentro(rootfs, destinoRelativo),
                    destinoEhDiretorio: destinoRelativo.EndsWith('/') || destinoRelativo.EndsWith('\\'));
                break;

            case "ENV":
                var (chave, valor) = ArquivoBuild.SepararEnv(instrucao.Argumentos);
                imagem.Ambiente[chave] = valor;
                break;

            case "CMD":
                imagem.Comando = instrucao.Argumentos;
                break;

            case "RUN":
                ExecutarPassoRun(instrucao, imagem, rootfs, saida);
                break;
        }

        imagem.Historico.Add(instrucao.ToString());
    }

    /// <summary>
    /// O <c>RUN</c> roda num container temporário: o mesmo Job Object com as
    /// mesmas restrições e o mesmo ambiente isolado, tendo o rootfs da imagem
    /// em construção como diretório de trabalho. O que ele escrever ali vira
    /// parte da imagem.
    /// </summary>
    private static void ExecutarPassoRun(InstrucaoBuild instrucao, Imagem imagem, string rootfs, Action<string> saida)
    {
        using var trabalho = new ObjetoTrabalho(LimitesRecursos.Nenhum, $@"Local\MiniDocker-build-{Guid.NewGuid():N}");
        var resultado = ExecutorProcesso.Executar(
            trabalho, instrucao.Argumentos, rootfs, imagem.Ambiente, linha => saida($" ---> {linha}"));

        if (resultado.CodigoSaida != 0)
        {
            throw new InvalidOperationException(
                $"Linha {instrucao.Linha}: '{instrucao}' terminou com codigo {resultado.CodigoSaida}.");
        }
    }

    private static void Copiar(string origem, string destino, bool destinoEhDiretorio)
    {
        if (Directory.Exists(origem))
        {
            // Como no Docker: copia o conteúdo do diretório, não o diretório em si.
            Workspace.CopiarDiretorio(origem, destino);
            return;
        }

        if (!File.Exists(origem))
        {
            throw new FileNotFoundException($"COPY: '{origem}' nao existe no contexto de build.");
        }

        if (destinoEhDiretorio || Directory.Exists(destino))
        {
            destino = Path.Combine(destino, Path.GetFileName(origem));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Copy(origem, destino, overwrite: true);
    }

    /// <summary>
    /// Move o diretório preparado para o lugar definitivo e grava o registro.
    /// Uma imagem anterior com a mesma referência é substituída, e seus
    /// arquivos apagados — os containers já criados dela não são afetados,
    /// porque cada um tem a própria cópia do rootfs.
    /// </summary>
    private Imagem Registrar(Imagem imagem, string diretorioPreparado)
    {
        var destino = Path.Combine(raizImagens, imagem.Id);
        Workspace.ApagarDiretorio(destino);
        Directory.Move(diretorioPreparado, destino);

        imagem.CaminhoRootFs = Path.Combine(destino, PastaRootFs);
        imagem.TamanhoBytes = Workspace.TamanhoDiretorio(imagem.CaminhoRootFs);

        if (repositorio.Salvar(imagem) is { } substituida)
        {
            Workspace.ApagarDiretorio(Path.Combine(raizImagens, substituida.Id));
        }

        return imagem;
    }

    private Imagem? TentarObter(string referenciaOuId)
    {
        try
        {
            return Obter(referenciaOuId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TentarNormalizar(string referencia)
    {
        try
        {
            return ReferenciaImagem.Normalizar(referencia);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string NomeEntrada(string raiz, string caminho) =>
        $"{PastaRootFs}/{Path.GetRelativePath(raiz, caminho).Replace('\\', '/')}";
}
