using System.Diagnostics;
using System.Text;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos.Runtimes;

/// <summary>
/// Imagem Linux num host Windows. Um programa Linux faz chamadas de sistema do
/// kernel Linux, que o kernel do Windows não atende — então é preciso um
/// kernel Linux, e no Windows ele vem do WSL2, uma máquina virtual leve. É o
/// mesmo que o Docker Desktop faz.
///
/// Cada container vira uma "distro" do WSL, importada do .tar da imagem. O
/// wsl.exe roda dentro do Job Object do container, mas o processo Linux de
/// verdade roda na VM: por isso limites e contabilidade não se aplicam aqui.
/// </summary>
internal sealed class RuntimeWsl : IRuntime
{
    public bool AplicaLimites => false;

    public void PrepararRootFs(Imagem imagem, Conteiner conteiner)
    {
        // O disco virtual da distro (ext4.vhdx) fica na pasta do container.
        Directory.CreateDirectory(conteiner.CaminhoRootFs);
        Ferramentas.Executar("wsl.exe", "--import", Distro(conteiner), conteiner.CaminhoRootFs,
            imagem.CaminhoRootFs, "--version", "2");
    }

    public ProcessStartInfo CriarProcesso(Conteiner conteiner, string comando)
    {
        var informacoes = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        informacoes.Environment["WSL_UTF8"] = "1";
        foreach (var argumento in new[] { "-d", Distro(conteiner), "--cd", "/", "--exec" })
        {
            informacoes.ArgumentList.Add(argumento);
        }

        SeletorRuntime.AdicionarComandoLinux(informacoes, conteiner, comando);
        return informacoes;
    }

    public ProcessStartInfo CriarProcessoExec(Conteiner conteiner, string comando, IGrupoProcessos grupo) =>
        CriarProcesso(conteiner, comando);

    /// <summary>
    /// Matar o wsl.exe não mata o processo dentro da VM; quem desliga a
    /// distro, e tudo o que roda nela, é o --terminate.
    /// </summary>
    public void AposTermino(Conteiner conteiner) =>
        Ferramentas.Tentar("wsl.exe", "--terminate", Distro(conteiner));

    public void Remover(Conteiner conteiner)
    {
        Ferramentas.Tentar("wsl.exe", "--unregister", Distro(conteiner));
        Workspace.ApagarDiretorio(conteiner.CaminhoRootFs);
    }

    private static string Distro(Conteiner conteiner) => $"MiniDocker-{conteiner.Id}";
}
