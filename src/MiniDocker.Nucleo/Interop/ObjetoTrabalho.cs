using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// Envólucro sobre um Job Object do Windows. O container passa a ser o grupo de
/// processos controlado por este objeto, e não mais um processo solto:
///
/// <list type="bullet">
/// <item>os limites de memória, de nº de processos e de CPU são impostos pelo kernel;</item>
/// <item>a contabilidade cobre todos os netos, não só o processo lançado;</item>
/// <item>fechar o handle encerra o grupo inteiro, sem deixar órfãos.</item>
/// </list>
/// </summary>
public sealed class ObjetoTrabalho : IDisposable
{
    private const long UnidadesPorMilissegundo = 10_000; // intervalos de 100 ns
    private const int ErroAcessoNegado = 5; // ERROR_ACCESS_DENIED

    private readonly SafeJobHandle handle;

    public ObjetoTrabalho(LimitesRecursos limites)
    {
        ArgumentNullException.ThrowIfNull(limites);
        limites.Validar();

        handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel criar o Job Object do container.");
        }

        AplicarLimitesEstendidos(limites);
        AplicarLimiteCpu(limites);
    }

    /// <summary>
    /// Move o processo para dentro do grupo. A partir daqui todo processo que
    /// ele criar nasce dentro do mesmo Job Object e herda os limites.
    /// </summary>
    /// <returns>
    /// <c>false</c> quando o processo terminou antes de ser atribuído. Existe uma
    /// janela entre <c>Process.Start</c> e a atribuição porque a classe
    /// <see cref="Process"/> não expõe <c>CREATE_SUSPENDED</c>; fechá-la exige
    /// chamar <c>CreateProcess</c> direto. Na prática a janela é de microssegundos
    /// contra os milissegundos de inicialização do <c>cmd.exe</c>, mas um comando
    /// que termine nela fica sem contabilidade de grupo — e, como já terminou,
    /// também não há mais nada a limitar.
    /// </returns>
    public bool Atribuir(Process processo)
    {
        ArgumentNullException.ThrowIfNull(processo);

        if (NativeMethods.AssignProcessToJobObject(handle, processo.Handle))
        {
            return true;
        }

        var erro = Marshal.GetLastWin32Error();
        if (erro == ErroAcessoNegado && processo.HasExited)
        {
            return false;
        }

        throw new Win32Exception(erro, "Nao foi possivel atribuir o processo ao Job Object.");
    }

    /// <summary>
    /// Lê o consumo acumulado do grupo. Continua funcionando depois que os
    /// processos terminam, ao contrário das propriedades de <see cref="Process"/>.
    /// </summary>
    public ContabilidadeTrabalho Contabilizar()
    {
        if (!NativeMethods.QueryInformationJobObject(
                handle,
                ClasseInformacaoTrabalho.ContabilidadeBasica,
                out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION contabilidade,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(),
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel consultar a contabilidade do Job Object.");
        }

        if (!NativeMethods.QueryInformationJobObject(
                handle,
                ClasseInformacaoTrabalho.LimitesEstendidos,
                out JOBOBJECT_EXTENDED_LIMIT_INFORMATION limites,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(),
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel consultar o pico de memoria do Job Object.");
        }

        var tempoTotal = contabilidade.TotalUserTime + contabilidade.TotalKernelTime;

        return new ContabilidadeTrabalho(
            TempoCpuMs: (double)tempoTotal / UnidadesPorMilissegundo,
            PicoMemoriaBytes: (long)limites.PeakJobMemoryUsed,
            TotalProcessos: (int)contabilidade.TotalProcesses,
            FalhasDePagina: contabilidade.TotalPageFaultCount);
    }

    /// <summary>Encerra de uma vez todos os processos do grupo.</summary>
    public void Encerrar(int codigoSaida = 1)
    {
        if (!NativeMethods.TerminateJobObject(handle, (uint)codigoSaida))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel encerrar o Job Object.");
        }
    }

    public void Dispose() => handle.Dispose();

    private void AplicarLimitesEstendidos(LimitesRecursos limites)
    {
        var informacao = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);

        // Sem este sinalizador, fechar o handle deixaria os processos do
        // container rodando soltos como órfãos.
        var sinalizadores = LimitFlags.KillOnJobClose;

        if (limites.MemoriaMaximaBytes is { } memoria)
        {
            sinalizadores |= LimitFlags.MemoriaPorProcesso | LimitFlags.MemoriaDoTrabalho;
            informacao.ProcessMemoryLimit = (nuint)memoria;
            informacao.JobMemoryLimit = (nuint)memoria;
        }

        if (limites.ProcessosMaximos is { } processos)
        {
            sinalizadores |= LimitFlags.ProcessosAtivos;
            informacao.BasicLimitInformation.ActiveProcessLimit = (uint)processos;
        }

        informacao.BasicLimitInformation.LimitFlags = (uint)sinalizadores;

        if (!NativeMethods.SetInformationJobObject(
                handle,
                ClasseInformacaoTrabalho.LimitesEstendidos,
                ref informacao,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel aplicar os limites de memoria e de processos.");
        }
    }

    private void AplicarLimiteCpu(LimitesRecursos limites)
    {
        if (limites.PercentualMaximoCpu is not { } percentual)
        {
            return;
        }

        var informacao = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = (uint)(CpuRateFlags.Habilitado | CpuRateFlags.TetoRigido),
            // A API espera o percentual multiplicado por 100.
            CpuRate = (uint)(percentual * 100)
        };

        if (!NativeMethods.SetInformationJobObject(
                handle,
                ClasseInformacaoTrabalho.ControleTaxaCpu,
                ref informacao,
                (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Nao foi possivel aplicar o limite de CPU.");
        }
    }
}
