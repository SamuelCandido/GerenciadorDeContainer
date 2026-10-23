using System.Diagnostics;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// O grupo de processos de um container: tudo o que ele cria fica dentro, e
/// encerrar o grupo encerra todos de uma vez. No Windows é um Job Object; no
/// Linux, um namespace de PID.
/// </summary>
internal interface IGrupoProcessos : IDisposable
{
    /// <summary>Põe o processo principal no grupo.</summary>
    bool Atribuir(Process processo);

    /// <summary>Consumo do grupo, ou <c>null</c> quando a plataforma não mede.</summary>
    ContabilidadeTrabalho? Contabilizar();

    /// <summary>Mata todos os processos do grupo (o <c>stop</c>).</summary>
    void Encerrar(int codigoSaida);

    /// <summary>Mata o que sobrou depois que o processo principal terminou.</summary>
    void EncerrarRestantes();
}

/// <summary>Escolhe a implementação do grupo de acordo com o sistema do host.</summary>
internal static class GruposDeProcessos
{
    public static IGrupoProcessos Criar(LimitesRecursos limites, string nome) =>
        OperatingSystem.IsWindows() ? new ObjetoTrabalho(limites, nome) : GrupoLinux.Criar(nome);

    public static IGrupoProcessos? Abrir(string nome) =>
        OperatingSystem.IsWindows() ? ObjetoTrabalho.Abrir(nome) : GrupoLinux.Abrir(nome);

    /// <summary>O container está vivo enquanto o seu grupo existir.</summary>
    public static bool Existe(string nome)
    {
        using var grupo = Abrir(nome);
        return grupo is not null;
    }
}
