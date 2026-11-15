using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniDocker.Nucleo.Interop;

/// <summary>Os tipos de mensagem que o kernel posta para um Job Object (<c>JOB_OBJECT_MSG_*</c>).</summary>
public enum TipoEventoKernel : uint
{
    LimiteDeProcessos = 3,   // ACTIVE_PROCESS_LIMIT: uma criação de processo foi recusada
    GrupoVazio = 4,          // ACTIVE_PROCESS_ZERO
    NovoProcesso = 6,        // NEW_PROCESS
    FimDeProcesso = 7,       // EXIT_PROCESS
    FimAnormal = 8,          // ABNORMAL_EXIT_PROCESS: terminou por exceção (código NTSTATUS)
    LimiteDeMemoria = 9,     // PROCESS_MEMORY_LIMIT: uma alocação passou do teto
    LimiteDeMemoriaGrupo = 10 // JOB_MEMORY_LIMIT
}

/// <summary>Uma mensagem do kernel sobre um processo do grupo.</summary>
public sealed record EventoKernel(TipoEventoKernel Tipo, int Pid, int? CodigoSaida)
{
    /// <summary>Eventos que indicam um problema, e não o funcionamento normal.</summary>
    public bool EhErro => Tipo is TipoEventoKernel.FimAnormal or TipoEventoKernel.LimiteDeMemoria
        or TipoEventoKernel.LimiteDeMemoriaGrupo or TipoEventoKernel.LimiteDeProcessos;

    public string Descricao => Tipo switch
    {
        TipoEventoKernel.LimiteDeProcessos => "limite de processos atingido: criacao de processo recusada",
        TipoEventoKernel.LimiteDeMemoria => $"limite de memoria atingido pelo processo {Pid}",
        TipoEventoKernel.LimiteDeMemoriaGrupo => "limite de memoria do container atingido",
        TipoEventoKernel.FimAnormal => $"processo {Pid} terminou com falha: {CodigosSaida.Descrever(CodigoSaida)}",
        TipoEventoKernel.NovoProcesso => $"processo {Pid} criado",
        TipoEventoKernel.FimDeProcesso => $"processo {Pid} terminou",
        _ => Tipo.ToString()
    };
}

/// <summary>
/// Lê os eventos de um Job Object pela <b>porta de conclusão de E/S</b>
/// associada a ele. Em vez de ficar consultando o grupo (<i>polling</i>), uma
/// thread dorme em <c>GetQueuedCompletionStatus</c> e o kernel a acorda quando
/// algo acontece: um processo morreu com exceção, estourou a memória, foi
/// barrado pelo limite de processos. É a base do <c>docker events</c> e do
/// <c>OOMKilled</c>.
/// </summary>
/// <remarks>
/// A documentação avisa que a entrega dessas mensagens não é garantida (a
/// fila pode encher). Serve para diagnóstico, não para contabilidade — essa
/// continua vindo de <c>QueryInformationJobObject</c>.
/// </remarks>
public sealed class MonitorEventosKernel : IDisposable
{
    private const uint Encerrar = 0xFFFFFFFF;
    private const uint ConsultaLimitada = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION
    private static readonly IntPtr HandleInvalido = new(-1);

    private readonly IntPtr porta;
    private readonly Thread leitor;

    internal MonitorEventosKernel(SafeJobHandle trabalho, Action<EventoKernel> aoReceber)
    {
        porta = NativeMethods.CreateIoCompletionPort(HandleInvalido, IntPtr.Zero, IntPtr.Zero, 1);
        if (porta == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel criar a porta de conclusao.");
        }

        var associacao = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT { CompletionKey = IntPtr.Zero, CompletionPort = porta };
        if (!NativeMethods.SetInformationJobObject(
                trabalho, ClasseInformacaoTrabalho.PortaDeConclusao, ref associacao,
                (uint)Marshal.SizeOf<JOBOBJECT_ASSOCIATE_COMPLETION_PORT>()))
        {
            var erro = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(porta);
            throw new Win32Exception(erro, "Nao foi possivel associar a porta de conclusao ao Job Object.");
        }

        leitor = new Thread(() => Ler(aoReceber)) { IsBackground = true, Name = "minidocker-eventos" };
        leitor.Start();
    }

    private void Ler(Action<EventoKernel> aoReceber)
    {
        while (NativeMethods.GetQueuedCompletionStatus(porta, out var mensagem, out _, out var pid, uint.MaxValue))
        {
            if (mensagem == Encerrar)
            {
                return;
            }

            if (!Enum.IsDefined(typeof(TipoEventoKernel), mensagem))
            {
                continue;
            }

            var tipo = (TipoEventoKernel)mensagem;
            var codigo = tipo == TipoEventoKernel.FimAnormal ? LerCodigoSaida((int)pid) : null;

            try
            {
                aoReceber(new EventoKernel(tipo, (int)pid, codigo));
            }
            catch (Exception)
            {
                // Um erro ao registrar um evento não pode derrubar o dono do container.
            }
        }
    }

    /// <summary>
    /// O evento traz só o PID. O código de saída é lido do processo, que pode
    /// já ter sido liberado — por isso é melhor esforço.
    /// </summary>
    private static int? LerCodigoSaida(int pid)
    {
        var processo = NativeMethods.OpenProcess(ConsultaLimitada, false, pid);
        if (processo == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeMethods.GetExitCodeProcess(processo, out var codigo) ? unchecked((int)codigo) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(processo);
        }
    }

    public void Dispose()
    {
        // Uma mensagem própria acorda a thread para ela sair; as mensagens que
        // o kernel postou antes ainda são entregues, porque a fila é FIFO.
        NativeMethods.PostQueuedCompletionStatus(porta, Encerrar, IntPtr.Zero, IntPtr.Zero);
        leitor.Join(TimeSpan.FromSeconds(2));
        NativeMethods.CloseHandle(porta);
    }
}

/// <summary>Tradução dos códigos de saída mais comuns.</summary>
public static class CodigosSaida
{
    public static string Descrever(int? codigo) => codigo switch
    {
        null => "codigo desconhecido",
        0 => "0 (sucesso)",
        137 => "137 (encerrado pelo stop)",
        unchecked((int)0xC0000005) => "0xC0000005 (violacao de acesso a memoria)",
        unchecked((int)0xC00000FD) => "0xC00000FD (estouro de pilha)",
        unchecked((int)0xC0000017) => "0xC0000017 (sem memoria)",
        unchecked((int)0xC000013A) => "0xC000013A (interrompido com Ctrl+C)",
        unchecked((int)0xC0000409) => "0xC0000409 (falha detectada pelo proprio programa)",
        unchecked((int)0xC0000135) => "0xC0000135 (DLL nao encontrada)",
        < 0 => $"0x{codigo:X8}",
        _ => codigo.Value.ToString()
    };

    /// <summary>Código de exceção do Windows (NTSTATUS de erro), e não um código escolhido pelo programa.</summary>
    public static bool EhFalha(int codigo) =>
        (codigo & unchecked((int)0xC0000000)) == unchecked((int)0xC0000000);
}
