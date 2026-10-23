using System.Diagnostics;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// O grupo de processos de um container no Linux. O <c>unshare</c> cria um
/// namespace de PID novo, e o primeiro processo dentro dele vira o PID 1 — o
/// "init" do container. Quando o init morre, o kernel mata todos os outros
/// processos do namespace: é o equivalente do KILL_ON_JOB_CLOSE do Job Object.
///
/// Para saber se o container está vivo, o processo dono segura uma trava num
/// arquivo. A trava é solta pelo kernel quando o dono morre, do mesmo jeito
/// que o nome de um Job Object some quando o último handle é fechado.
/// </summary>
internal sealed class GrupoLinux : IGrupoProcessos
{
    private static readonly string Diretorio = Path.Combine(Path.GetTempPath(), "minidocker");

    private readonly string caminhoPid;
    private readonly string caminhoTrava;

    /// <summary>Só o processo dono segura a trava; quem só abriu o grupo não.</summary>
    private readonly FileStream? trava;

    private GrupoLinux(string nome, FileStream? trava)
    {
        caminhoPid = Path.Combine(Diretorio, NomeArquivo(nome) + ".pid");
        caminhoTrava = CaminhoTrava(nome);
        this.trava = trava;
    }

    public static GrupoLinux Criar(string nome)
    {
        Directory.CreateDirectory(Diretorio);
        try
        {
            var trava = new FileStream(CaminhoTrava(nome), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new GrupoLinux(nome, trava);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("O container ja esta em execucao.");
        }
    }

    /// <summary>Devolve o grupo se algum processo dono ainda segura a trava.</summary>
    public static GrupoLinux? Abrir(string nome)
    {
        try
        {
            using var teste = new FileStream(CaminhoTrava(nome), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            return null; // conseguiu travar: ninguém é dono, o container morreu
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return new GrupoLinux(nome, trava: null);
        }
    }

    /// <summary>PID do <c>unshare</c>, o processo principal visto pelo host.</summary>
    public int? IdPrincipal =>
        File.Exists(caminhoPid) && int.TryParse(File.ReadAllText(caminhoPid), out var pid) ? pid : null;

    /// <summary>
    /// PID, visto pelo host, do init do container — o filho do <c>unshare</c>,
    /// que dentro do namespace é o PID 1. É nele que o <c>exec</c> entra.
    /// </summary>
    public int? IdInit
    {
        get
        {
            if (IdPrincipal is not { } pid)
            {
                return null;
            }

            var filhos = Path.Combine("/proc", pid.ToString(), "task", pid.ToString(), "children");
            return File.Exists(filhos) && int.TryParse(File.ReadAllText(filhos).Split(' ')[0], out var init)
                ? init
                : null;
        }
    }

    public bool Atribuir(Process processo)
    {
        // O processo já nasce no namespace (é o unshare que o cria); aqui só se
        // anota o PID para que o stop, vindo de outro processo, saiba quem matar.
        if (trava is not null)
        {
            File.WriteAllText(caminhoPid, processo.Id.ToString());
        }

        return true;
    }

    /// <summary>Sem cgroups, o MiniDocker não mede o consumo no Linux.</summary>
    public ContabilidadeTrabalho? Contabilizar() => null;

    /// <summary>
    /// Envia SIGKILL ao <c>unshare</c>. Com <c>--kill-child</c>, ele leva junto
    /// o init do container, e com o init o namespace inteiro. O código de saída
    /// é sempre 137 (128 + SIGKILL), que é o que o <c>stop</c> registra.
    /// </summary>
    public void Encerrar(int codigoSaida)
    {
        if (IdPrincipal is not { } pid)
        {
            return;
        }

        try
        {
            using var processo = Process.GetProcessById(pid);
            processo.Kill();
        }
        catch (Exception excecao) when (excecao is ArgumentException or InvalidOperationException)
        {
            // Já tinha terminado.
        }
    }

    /// <summary>Nada a fazer: quando o processo principal termina, o namespace morre junto.</summary>
    public void EncerrarRestantes()
    {
    }

    public void Dispose()
    {
        if (trava is not null)
        {
            // Apagar antes de soltar: quem procurar o grupo a partir daqui não
            // acha o arquivo e conclui que o container terminou — e terminou.
            File.Delete(caminhoPid);
            File.Delete(caminhoTrava);
            trava.Dispose();
        }
    }

    private static string CaminhoTrava(string nome) =>
        Path.Combine(Diretorio, NomeArquivo(nome) + ".lock");

    // "Local\MiniDocker-abc" é o nome do Job Object; aqui vira nome de arquivo.
    private static string NomeArquivo(string nome) => nome.Replace('\\', '-');
}
