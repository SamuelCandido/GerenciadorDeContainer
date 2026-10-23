using System.Diagnostics;
using System.Text;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos.Runtimes;

/// <summary>
/// Imagem Linux num host Linux: aqui o isolamento é o de verdade, o mesmo que
/// o Docker usa. O <c>unshare</c> cria namespaces novos e o <c>chroot</c> troca
/// a raiz do sistema de arquivos:
///
/// <list type="table">
/// <item><term>--pid</term><description>o container vê só os próprios processos; o primeiro é o PID 1</description></item>
/// <item><term>--mount</term><description>o /proc e os /dev montados aqui não aparecem no host</description></item>
/// <item><term>--uts</term><description>hostname próprio: o nome do container</description></item>
/// <item><term>--ipc</term><description>memória compartilhada e semáforos próprios</description></item>
/// <item><term>--user</term><description>"root" dentro do container, usuário comum fora — não precisa de sudo</description></item>
/// </list>
///
/// Rede e limites de recursos (cgroups) ficaram de fora para manter o código pequeno.
/// </summary>
internal sealed class RuntimeLinux : IRuntime
{
    /// <summary>
    /// Roda dentro dos namespaces novos, ainda com a raiz do host: monta /proc
    /// e alguns dispositivos dentro do rootfs, define o hostname e entra no
    /// rootfs com chroot. Recebe o rootfs e o nome como $1 e $2.
    /// </summary>
    private const string Preparacao = """
        R="$1"; N="$2"; shift 2
        mkdir -p "$R/proc" "$R/dev"
        for d in null zero random urandom; do
            [ -e "$R/dev/$d" ] || touch "$R/dev/$d"
            mount --bind "/dev/$d" "$R/dev/$d"
        done
        mount -t proc proc "$R/proc"
        hostname "$N"
        exec chroot "$R" "$@"
        """;

    public bool AplicaLimites => false;

    public void PrepararRootFs(Imagem imagem, Conteiner conteiner)
    {
        // A imagem guarda a camada como um .tar; cada container ganha a sua
        // cópia extraída. --no-same-owner: os arquivos ficam com o usuário
        // atual, que dentro do container é visto como root.
        Directory.CreateDirectory(conteiner.CaminhoRootFs);
        Ferramentas.Executar("tar", "-xf", imagem.CaminhoRootFs, "-C", conteiner.CaminhoRootFs, "--no-same-owner");
    }

    public ProcessStartInfo CriarProcesso(Conteiner conteiner, string comando)
    {
        var informacoes = Novo("unshare");
        foreach (var argumento in new[]
                 {
                     "--user", "--map-root-user", "--pid", "--fork", "--kill-child",
                     "--mount", "--uts", "--ipc",
                     "/bin/sh", "-c", Preparacao, "minidocker", conteiner.CaminhoRootFs,
                     SeletorRuntime.NomeHost(conteiner)
                 })
        {
            informacoes.ArgumentList.Add(argumento);
        }

        SeletorRuntime.AdicionarComandoLinux(informacoes, conteiner, comando);
        return informacoes;
    }

    /// <summary>
    /// O <c>nsenter</c> entra nos mesmos namespaces e na mesma raiz do init do
    /// container. O processo novo enxerga o que o container enxerga e morre
    /// junto com ele.
    /// </summary>
    public ProcessStartInfo CriarProcessoExec(Conteiner conteiner, string comando, IGrupoProcessos grupo)
    {
        var init = (grupo as GrupoLinux)?.IdInit
            ?? throw new InvalidOperationException($"O container '{conteiner.Nome}' nao esta em execucao.");

        var informacoes = Novo("nsenter");
        foreach (var argumento in new[]
                 {
                     "--target", init.ToString(), "--user", "--preserve-credentials",
                     "--mount", "--pid", "--uts", "--ipc", "--root", "--wd"
                 })
        {
            informacoes.ArgumentList.Add(argumento);
        }

        SeletorRuntime.AdicionarComandoLinux(informacoes, conteiner, comando);
        return informacoes;
    }

    public void Remover(Conteiner conteiner)
    {
        if (Directory.Exists(conteiner.CaminhoRootFs))
        {
            // Algumas pastas da imagem vêm sem permissão de escrita (/proc, /sys).
            Ferramentas.Executar("chmod", "-R", "u+rwX", conteiner.CaminhoRootFs);
            Workspace.ApagarDiretorio(conteiner.CaminhoRootFs);
        }
    }

    private static ProcessStartInfo Novo(string arquivo) => new()
    {
        FileName = arquivo,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };
}
