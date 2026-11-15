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
    PortaDeConclusao = 7,
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

/// <summary>Liga o Job Object a uma porta de conclusão, onde o kernel posta os eventos do grupo.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
{
    public IntPtr CompletionKey;
    public IntPtr CompletionPort;
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

    /// <summary>
    /// Abre um Job Object existente pelo nome. É o que permite a outro processo
    /// — o <c>stop</c>, o <c>exec</c>, o <c>stats</c> — alcançar o grupo criado
    /// pelo processo dono do container.
    /// </summary>
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeJobHandle OpenJobObjectW(
        uint acessoDesejado,
        [MarshalAs(UnmanagedType.Bool)] bool herdarHandle,
        string nome);

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
    internal static extern bool SetInformationJobObject(
        SafeJobHandle trabalho,
        ClasseInformacaoTrabalho classe,
        ref JOBOBJECT_ASSOCIATE_COMPLETION_PORT informacao,
        uint tamanho);

    /// <summary>
    /// Com <c>arquivo = INVALID_HANDLE_VALUE</c>, cria uma porta de conclusão
    /// sem arquivo associado: uma fila de mensagens do kernel.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr CreateIoCompletionPort(
        IntPtr arquivo, IntPtr portaExistente, IntPtr chave, uint threadsConcorrentes);

    /// <summary>
    /// Bloqueia até o kernel postar uma mensagem. Para um Job Object, o
    /// "número de bytes" é o tipo da mensagem e o "overlapped" é o PID.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetQueuedCompletionStatus(
        IntPtr porta, out uint mensagem, out IntPtr chave, out IntPtr pid, uint milissegundos);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostQueuedCompletionStatus(IntPtr porta, uint mensagem, IntPtr chave, IntPtr pid);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint acesso, [MarshalAs(UnmanagedType.Bool)] bool herdar, int pid);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr processo, out uint codigoSaida);

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

/// <summary>Handle genérico do kernel (processo, thread, token), fechado com <c>CloseHandle</c>.</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeKernelHandle() : base(ownsHandle: true)
    {
    }

    public SafeKernelHandle(IntPtr handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFO
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SECURITY_ATTRIBUTES
{
    public int nLength;
    public IntPtr lpSecurityDescriptor;
    public int bInheritHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SID_AND_ATTRIBUTES
{
    public IntPtr Sid;
    public uint Attributes;
}

/// <summary>O rótulo de integridade de um token: um SID do tipo <c>S-1-16-*</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_MANDATORY_LABEL
{
    public SID_AND_ATTRIBUTES Label;
}

/// <summary>
/// Criação de processos e segurança: o que a classe <c>Process</c> do .NET
/// esconde. Necessário para criar o processo suspenso (e só liberá-lo depois
/// de dentro do Job Object) e com um token de integridade baixa.
/// </summary>
internal static class NativeMetodosProcesso
{
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";

    public const uint CreateSuspended = 0x00000004;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateNoWindow = 0x08000000;
    public const int StartfUseStdHandles = 0x00000100;
    public const uint Infinito = 0xFFFFFFFF;
    public const uint HandleFlagInherit = 0x00000001;

    public const uint TokenQuery = 0x0008;
    public const uint TokenDuplicate = 0x0002;
    public const uint TokenAssignPrimary = 0x0001;
    public const uint TokenAdjustDefault = 0x0080;
    public const uint MaximumAllowed = 0x02000000;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenIntegrityLevel = 25;
    public const uint SeGroupIntegrity = 0x00000020;

    public const int SeFileObject = 1;
    public const uint LabelSecurityInformation = 0x00000010;

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? nomeAplicacao,
        System.Text.StringBuilder linhaComando,
        IntPtr atributosProcesso,
        IntPtr atributosThread,
        [MarshalAs(UnmanagedType.Bool)] bool herdarHandles,
        uint sinalizadores,
        IntPtr ambiente,
        string? diretorioAtual,
        ref STARTUPINFO inicio,
        out PROCESS_INFORMATION informacao);

    /// <summary>
    /// Como <c>CreateProcess</c>, mas o processo nasce com o token informado.
    /// Um usuário comum pode usar uma cópia do próprio token com integridade
    /// rebaixada — é assim que o modo protegido dos navegadores funciona.
    /// </summary>
    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUserW(
        SafeKernelHandle token,
        string? nomeAplicacao,
        System.Text.StringBuilder linhaComando,
        IntPtr atributosProcesso,
        IntPtr atributosThread,
        [MarshalAs(UnmanagedType.Bool)] bool herdarHandles,
        uint sinalizadores,
        IntPtr ambiente,
        string? diretorioAtual,
        ref STARTUPINFO inicio,
        out PROCESS_INFORMATION informacao);

    /// <summary>Decrementa o contador de suspensão; em zero, o escalonador passa a executar a thread.</summary>
    [DllImport(Kernel32, SetLastError = true)]
    internal static extern uint ResumeThread(SafeKernelHandle thread);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeKernelHandle processo, uint codigoSaida);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeKernelHandle objeto, uint milissegundos);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeKernelHandle processo, out uint codigoSaida);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle leitura, out SafeFileHandle escrita, ref SECURITY_ATTRIBUTES atributos, uint tamanho);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(SafeHandle handle, uint mascara, uint sinalizadores);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string nome, uint acesso, uint compartilhamento, ref SECURITY_ATTRIBUTES atributos,
        uint disposicao, uint sinalizadores, IntPtr modelo);

    [DllImport(Kernel32)]
    internal static extern uint GetConsoleOutputCP();

    [DllImport(Kernel32)]
    internal static extern uint GetOEMCP();

    [DllImport(Kernel32)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr memoria);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processo, uint acesso, out SafeKernelHandle token);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        SafeKernelHandle token, uint acesso, IntPtr atributos, int nivelPersonificacao, int tipo,
        out SafeKernelHandle novoToken);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenInformation(
        SafeKernelHandle token, int classe, ref TOKEN_MANDATORY_LABEL informacao, uint tamanho);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSidToSidW(string sid, out IntPtr resultado);

    [DllImport(Advapi32)]
    internal static extern uint GetLengthSid(IntPtr sid);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string descritor, uint revisao, out IntPtr resultado, IntPtr tamanho);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSecurityDescriptorSacl(
        IntPtr descritor,
        [MarshalAs(UnmanagedType.Bool)] out bool presente,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool padrao);

    /// <summary>Devolve o código de erro em vez de usar o último erro da thread.</summary>
    [DllImport(Advapi32, CharSet = CharSet.Unicode)]
    internal static extern uint SetNamedSecurityInfoW(
        string objeto, int tipo, uint informacao, IntPtr dono, IntPtr grupo, IntPtr dacl, IntPtr sacl);
}
