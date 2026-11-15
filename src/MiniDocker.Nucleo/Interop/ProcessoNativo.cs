using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// Um processo criado direto com <c>CreateProcess</c>, sempre
/// <b>suspenso</b>. A thread principal nasce com o contador de suspensão em 1
/// e o escalonador não a executa até <see cref="Retomar"/>. Nesse intervalo o
/// processo é posto no Job Object, então ele não executa nenhuma instrução fora
/// do grupo — a corrida da Etapa 1 deixa de existir. É o que o Docker faz.
/// </summary>
internal sealed class ProcessoNativo : IDisposable
{
    private readonly SafeKernelHandle thread;

    private ProcessoNativo(PROCESS_INFORMATION informacao)
    {
        Handle = new SafeKernelHandle(informacao.hProcess);
        thread = new SafeKernelHandle(informacao.hThread);
        Id = informacao.dwProcessId;
    }

    public SafeKernelHandle Handle { get; }

    public int Id { get; }

    /// <param name="linhaComando">Linha de comando completa, já com as aspas.</param>
    /// <param name="token">Token do novo processo; <c>null</c> usa o deste processo.</param>
    /// <param name="saida">
    /// Ponta herdável de um pipe para stdout e stderr; <c>null</c> faz o
    /// processo usar o console de quem chamou (<c>-it</c>, <c>exec</c>).
    /// </param>
    /// <param name="entrada">Handle herdável para stdin, usado junto com <paramref name="saida"/>.</param>
    public static ProcessoNativo CriarSuspenso(
        string aplicacao,
        string linhaComando,
        string diretorio,
        IReadOnlyDictionary<string, string> ambiente,
        SafeKernelHandle? token,
        SafeFileHandle? saida,
        SafeFileHandle? entrada)
    {
        var inicio = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var sinalizadores = NativeMetodosProcesso.CreateSuspended | NativeMetodosProcesso.CreateUnicodeEnvironment;
        var redirecionar = saida is not null;

        if (redirecionar)
        {
            inicio.dwFlags = NativeMetodosProcesso.StartfUseStdHandles;
            inicio.hStdInput = entrada?.DangerousGetHandle() ?? IntPtr.Zero;
            inicio.hStdOutput = saida!.DangerousGetHandle();
            inicio.hStdError = saida.DangerousGetHandle();
            // Sem console próprio: o shim não tem console e o Windows abriria
            // uma janela nova para o cmd.exe.
            sinalizadores |= NativeMetodosProcesso.CreateNoWindow;
        }

        // O CreateProcess pode escrever na linha de comando; por isso um buffer
        // mutável, e não uma string.
        var comando = new StringBuilder(linhaComando, linhaComando.Length + 1);
        var blocoAmbiente = Marshal.StringToHGlobalUni(MontarBlocoAmbiente(ambiente));

        try
        {
            PROCESS_INFORMATION informacao;
            var criado = token is null
                ? NativeMetodosProcesso.CreateProcessW(
                    aplicacao, comando, IntPtr.Zero, IntPtr.Zero, redirecionar, sinalizadores,
                    blocoAmbiente, diretorio, ref inicio, out informacao)
                : NativeMetodosProcesso.CreateProcessAsUserW(
                    token, aplicacao, comando, IntPtr.Zero, IntPtr.Zero, redirecionar, sinalizadores,
                    blocoAmbiente, diretorio, ref inicio, out informacao);

            if (!criado)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel criar o processo do container.");
            }

            return new ProcessoNativo(informacao);
        }
        finally
        {
            Marshal.FreeHGlobal(blocoAmbiente);
        }
    }

    /// <summary>Libera a thread principal: só agora o processo começa a executar.</summary>
    public void Retomar()
    {
        if (NativeMetodosProcesso.ResumeThread(thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel retomar o processo.");
        }
    }

    public void Encerrar(uint codigoSaida = 1) => NativeMetodosProcesso.TerminateProcess(Handle, codigoSaida);

    /// <summary>Espera só este processo — não os netos nem o fim dos pipes.</summary>
    public int AguardarSaida()
    {
        NativeMetodosProcesso.WaitForSingleObject(Handle, NativeMetodosProcesso.Infinito);
        if (!NativeMetodosProcesso.GetExitCodeProcess(Handle, out var codigo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel ler o codigo de saida.");
        }

        return unchecked((int)codigo);
    }

    public void Dispose()
    {
        thread.Dispose();
        Handle.Dispose();
    }

    /// <summary>
    /// O bloco de ambiente que o <c>CreateProcess</c> espera: <c>CHAVE=valor\0</c>
    /// repetido, ordenado sem diferenciar maiúsculas, terminado por outro <c>\0</c>
    /// (acrescentado pelo <c>StringToHGlobalUni</c>).
    /// </summary>
    private static string MontarBlocoAmbiente(IReadOnlyDictionary<string, string> ambiente)
    {
        var bloco = new StringBuilder();
        foreach (var (chave, valor) in ambiente.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            bloco.Append(chave).Append('=').Append(valor).Append('\0');
        }

        if (bloco.Length == 0)
        {
            bloco.Append('\0');
        }

        return bloco.ToString();
    }
}
