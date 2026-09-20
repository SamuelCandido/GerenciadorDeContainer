using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public Conteiner Executar(string nome, string comando)
    {
        ValidarTexto(nome, nameof(nome));
        ValidarTexto(comando, nameof(comando));

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
            CaminhoRootFs = caminhoRootFs
        };
        repositorio.Salvar(conteiner);

        using var processo = CriarProcesso(comando, caminhoRootFs);
        conteiner.IniciadoEm = DateTime.UtcNow;
        conteiner.Estado = EstadoConteiner.Executando;
        processo.Start();
        conteiner.IdProcesso = processo.Id;
        repositorio.Salvar(conteiner);

        // A leitura ocorre enquanto o processo ainda pode ser consultado; após o
        // término, algumas plataformas deixam de disponibilizar estas propriedades.
        try
        {
            conteiner.MemoriaUsadaBytes = processo.WorkingSet64;
            conteiner.TempoCpuMs = processo.TotalProcessorTime.TotalMilliseconds;
        }
        catch (InvalidOperationException)
        {
            // Comandos instantâneos podem terminar nesta pequena janela de corrida.
            conteiner.MemoriaUsadaBytes = 0;
            conteiner.TempoCpuMs = 0;
        }
        processo.WaitForExit();
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
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = caminhoRootFs,
            UseShellExecute = false
        };
        informacoes.ArgumentList.Add(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "/c" : "-c");
        informacoes.ArgumentList.Add(comando);
        return new Process { StartInfo = informacoes };
    }
}