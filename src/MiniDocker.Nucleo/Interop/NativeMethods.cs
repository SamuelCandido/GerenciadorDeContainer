using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// Handle de um Job Object. Fechar o handle encerra os processos do grupo
/// quando o limite <see cref="LimitFlags.KillOnJobClose"/> está ativo.
/// </summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeJobHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>Classes de informação aceitas pela API de Job Objects.</summary>
internal enum ClasseInformacaoTrabalho
{
    ContabilidadeBasica = 1,
    RestricoesUIBasicas = 4,
    LimitesEstendidos = 9,
    ControleTaxaCpu = 15
}

/// <summary>
/// Restrições de interface que o Job Object impõe ao grupo. São o mecanismo de
/// isolamento documentado mais forte da API: separam o container do host em
/// recursos compartilhados do subsistema de janelas.
/// </summary>
[Flags]
internal enum RestricoesUI : uint
{
    /// <summary>Não pode usar handles de janela de processos de fora do grupo.</summary>
    Handles = 0x00000001,
    LerAreaTransferencia = 0x00000002,
    EscreverAreaTransferencia = 0x00000004,
    /// <summary>Não pode alterar parâmetros do sistema.</summary>
    ParametrosDoSistema = 0x00000008,
    ConfiguracoesDeVideo = 0x00000010,
    /// <summary>Recebe uma tabela de atoms globais própria — um namespace privado.</summary>
    AtomsGlobais = 0x00000020,
    /// <summary>Não pode criar nem trocar de desktop.</summary>
    Desktop = 0x00000040,
    /// <summary>Não pode desligar nem reiniciar o Windows.</summary>
    DesligarWindows = 0x00000080
}

/// <summary>Sinalizadores de quais campos de limite estão em uso.</summary>
[Flags]
internal enum LimitFlags : uint
{
    ProcessosAtivos = 0x00000008,
    MemoriaPorProcesso = 0x00000100,
    MemoriaDoTrabalho = 0x00000200,
    KillOnJobClose = 0x00002000
}

/// <summary>Sinalizadores do controle de taxa de CPU.</summary>
[Flags]
internal enum CpuRateFlags : uint
{
    Habilitado = 0x00000001,
    TetoRigido = 0x00000004
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>
/// Contabilidade acumulada do grupo. Os tempos vêm em unidades de 100 ns e
/// continuam válidos depois que os processos terminam, o que elimina a corrida
/// de ler <c>Process.WorkingSet64</c> com o processo ainda vivo.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
{
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
}

/// <summary>Restrições de interface aplicadas a todo o grupo.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_UI_RESTRICTIONS
{
    public uint UIRestrictionsClass;
}

/// <summary>União: o mesmo espaço guarda a taxa, o peso ou o par min/max.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    [FieldOffset(0)] public uint ControlFlags;
    [FieldOffset(4)] public uint CpuRate;
    [FieldOffset(4)] public uint Weight;
}

/// <summary>
/// Declarações P/Invoke da API de Job Objects do Windows. Um Job Object agrupa
/// processos, aplica limites de recursos ao grupo inteiro, contabiliza o consumo
/// e permite encerrar todos os processos de uma só vez. É o equivalente mais
/// próximo, no Windows, dos cgroups do Linux.
/// </summary>
internal static class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeJobHandle CreateJobObjectW(IntPtr atributos, string? nome);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeJobHandle trabalho, IntPtr processo);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION informacao,
        uint tamanho);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION informacao,
        uint tamanho);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        ref JOBOBJECT_BASIC_UI_RESTRICTIONS informacao,
        uint tamanho);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION informacao,
        uint tamanho,
        IntPtr tamanhoRetornado);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        out JOBOBJECT_EXTENDED_LIMIT_INFORMATION informacao,
        uint tamanho,
        IntPtr tamanhoRetornado);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(SafeJobHandle trabalho, uint codigoSaida);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
