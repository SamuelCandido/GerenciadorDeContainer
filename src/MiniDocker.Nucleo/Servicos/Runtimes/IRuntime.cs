using System.Diagnostics;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos.Runtimes;

/// <summary>
/// Como um container é montado e executado, de acordo com a plataforma da
/// imagem e o sistema do host:
///
/// <list type="table">
/// <item><term>imagem Windows, host Windows</term><description><see cref="RuntimeWindows"/>: cmd.exe num Job Object</description></item>
/// <item><term>imagem Linux, host Linux</term><description><see cref="RuntimeLinux"/>: namespaces + chroot</description></item>
/// <item><term>imagem Linux, host Windows</term><description><see cref="RuntimeWsl"/>: uma distro do WSL2</description></item>
/// </list>
/// </summary>
internal interface IRuntime
{
    /// <summary>Limites de CPU, memória e processos só existem com Job Object.</summary>
    bool AplicaLimites { get; }

    /// <summary>Monta o sistema de arquivos do container a partir da imagem.</summary>
    void PrepararRootFs(Imagem imagem, Conteiner conteiner);

    /// <summary>O processo principal do container.</summary>
    ProcessStartInfo CriarProcesso(Conteiner conteiner, string comando);

    /// <summary>Um processo a mais dentro de um container vivo (<c>exec</c>).</summary>
    ProcessStartInfo CriarProcessoExec(Conteiner conteiner, string comando, IGrupoProcessos grupo);

    /// <summary>Limpeza depois que o processo principal termina ou é morto.</summary>
    void AposTermino(Conteiner conteiner)
    {
    }

    /// <summary>Apaga o sistema de arquivos do container.</summary>
    void Remover(Conteiner conteiner);
}

internal static class SeletorRuntime
{
    public static IRuntime Para(string plataforma) => plataforma switch
    {
        Imagem.PlataformaLinux when OperatingSystem.IsWindows() => new RuntimeWsl(),
        Imagem.PlataformaLinux => new RuntimeLinux(),
        _ => new RuntimeWindows()
    };

    /// <summary>Hostname aceita letras, dígitos e '-'; nomes como "calmo_turing" viram "calmo-turing".</summary>
    public static string NomeHost(Conteiner conteiner) =>
        new(conteiner.Nome.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>
    /// Monta <c>env -i CHAVE=valor ... /bin/sh -c comando</c>: o comando roda
    /// num ambiente limpo, só com as variáveis da imagem e do <c>-e</c>.
    /// </summary>
    public static void AdicionarComandoLinux(ProcessStartInfo informacoes, Conteiner conteiner, string comando)
    {
        informacoes.ArgumentList.Add("/usr/bin/env");
        informacoes.ArgumentList.Add("-i");
        informacoes.ArgumentList.Add($"HOSTNAME={NomeHost(conteiner)}");
        informacoes.ArgumentList.Add("HOME=/root");
        if (!conteiner.Ambiente.ContainsKey("PATH"))
        {
            informacoes.ArgumentList.Add("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        }

        foreach (var (chave, valor) in conteiner.Ambiente)
        {
            informacoes.ArgumentList.Add($"{chave}={valor}");
        }

        informacoes.ArgumentList.Add("/bin/sh");
        informacoes.ArgumentList.Add("-c");
        informacoes.ArgumentList.Add(comando);
    }
}
