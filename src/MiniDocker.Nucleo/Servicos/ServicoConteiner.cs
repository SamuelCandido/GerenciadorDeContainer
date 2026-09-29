using System.Diagnostics;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Repositorios;

namespace MiniDocker.Nucleo.Servicos;

public class ServicoConteiner : IServicoConteiner
{
    private readonly IRepositorioConteiner repositorio;
    private readonly string raizWorkspace;

    public ServicoConteiner(string? raizWorkspace = null)
        : this(new RepositorioConteinerArquivo(raizWorkspace), raizWorkspace)
    {
    }

    public ServicoConteiner(IRepositorioConteiner repositorio, string? raizWorkspace = null)
    {
        this.repositorio = repositorio;
        this.raizWorkspace = raizWorkspace ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minidocker");
        Directory.CreateDirectory(Path.Combine(this.raizWorkspace, "containers"));
    }

    public Conteiner Executar(string nome, string comando, LimitesRecursos? limites = null)
    {
        ValidarTexto(nome, nameof(nome));
        ValidarTexto(comando, nameof(comando));

        // Validado antes de criar diretório ou registro, para que um limite
        // inválido não deixe um container pela metade no repositório.
        limites ??= LimitesRecursos.Nenhum;
        limites.Validar();

        if (repositorio.ObterPorNome(nome) is not null)
        {
            throw new InvalidOperationException($"Ja existe um container com o nome '{nome}'.");
        }

        var caminhoRootFs = Path.Combine(raizWorkspace, "containers", nome);
        Directory.CreateDirectory(caminhoRootFs);

        var conteiner = new Conteiner
        {
            Nome = nome,
            Comando = comando,
            CaminhoRootFs = caminhoRootFs,
            Limites = limites
        };
        repositorio.Salvar(conteiner);

        // O Job Object nasce antes do processo: assim que o processo é atribuído
        // ao grupo, todo descendente dele já nasce sujeito aos mesmos limites.
        using var trabalho = new ObjetoTrabalho(limites);
        using var processo = CriarProcesso(comando, caminhoRootFs);

        conteiner.IniciadoEm = DateTime.UtcNow;
        conteiner.Estado = EstadoConteiner.Executando;
        processo.Start();
        conteiner.IdProcesso = processo.Id;
        var noGrupo = trabalho.Atribuir(processo);
        repositorio.Salvar(conteiner);

        processo.WaitForExit();

        // A contabilidade do Job Object sobrevive ao fim dos processos, então a
        // leitura não corre contra o término: os números cobrem o grupo inteiro,
        // inclusive os netos que o comando tenha criado.
        var contabilidade = noGrupo
            ? trabalho.Contabilizar()
            : new ContabilidadeTrabalho(0, 0, 0, 0);
        conteiner.MemoriaUsadaBytes = contabilidade.PicoMemoriaBytes;
        conteiner.TempoCpuMs = contabilidade.TempoCpuMs;
        conteiner.TotalProcessos = contabilidade.TotalProcessos;
        conteiner.FalhasDePagina = contabilidade.FalhasDePagina;

        conteiner.CodigoSaida = processo.ExitCode;
        conteiner.FinalizadoEm = DateTime.UtcNow;
        conteiner.Estado = EstadoConteiner.Finalizado;
        repositorio.Salvar(conteiner);

        return conteiner;
    }

    public IReadOnlyList<Conteiner> Listar() => repositorio.ObterTodos();

    public void Parar(string nome)
    {
        if (repositorio.ObterPorNome(nome) is null)
        {
            throw new InvalidOperationException($"Container '{nome}' nao existe.");
        }

        throw new InvalidOperationException(
            "Nao ha processo em execucao para parar neste prototipo sincronio.");
    }

    public void Remover(string nome)
    {
        var conteiner = repositorio.ObterPorNome(nome)
            ?? throw new InvalidOperationException($"Container '{nome}' nao existe.");

        if (conteiner.Estado == EstadoConteiner.Executando)
        {
            throw new InvalidOperationException("Nao e possivel remover um container em execucao.");
        }

        repositorio.Remover(nome);
        if (Directory.Exists(conteiner.CaminhoRootFs))
        {
            Directory.Delete(conteiner.CaminhoRootFs, recursive: true);
        }
    }

    private static void ValidarTexto(string valor, string nomeParametro)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            throw new ArgumentException("O valor nao pode ser vazio.", nomeParametro);
        }
    }

    private static Process CriarProcesso(string comando, string caminhoRootFs)
    {
        var informacoes = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = caminhoRootFs,
            UseShellExecute = false
        };
        informacoes.ArgumentList.Add("/c");
        informacoes.ArgumentList.Add(comando);
        return new Process { StartInfo = informacoes };
    }
}