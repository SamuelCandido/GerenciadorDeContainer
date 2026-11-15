using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniDocker.Nucleo.Interop;

/// <summary>
/// Confinamento do sistema de arquivos por nível de integridade.
///
/// Todo processo e todo objeto do Windows têm um rótulo de integridade —
/// Low, Medium, High ou System. Um programa comum roda em Medium, e quase todo
/// o disco também é Medium (é o padrão de quem não tem rótulo). Por cima das
/// ACLs, o kernel aplica a regra <b>no-write-up</b>: um processo não escreve em
/// objetos de integridade maior que a dele, mesmo que a ACL deixe.
///
/// O container roda em Low. O rootfs e os volumes dele são rotulados Low, e só
/// ali ele consegue escrever. É o mecanismo do modo protegido dos navegadores:
/// controle de acesso obrigatório (MAC) sobreposto ao discricionário (ACLs).
/// </summary>
internal static class IntegridadeBaixa
{
    /// <summary>SID do nível de integridade baixo (<c>SECURITY_MANDATORY_LOW_RID</c>).</summary>
    private const string SidBaixa = "S-1-16-4096";

    /// <summary>
    /// SDDL de um rótulo obrigatório: <c>ML</c> (mandatory label), herdado por
    /// arquivos e subpastas (<c>OICI</c>), com a política no-write-up
    /// (<c>NW</c>), nível Low (<c>LW</c>).
    /// </summary>
    private const string RotuloBaixo = "S:(ML;OICI;NW;;;LW)";

    /// <summary>O mesmo rótulo, nível Medium (<c>ME</c>): o padrão do disco, escrito explicitamente.</summary>
    private const string RotuloMedio = "S:(ML;OICI;NW;;;ME)";

    /// <summary>
    /// Uma cópia do token deste processo com integridade rebaixada para Low.
    /// O usuário continua o mesmo — as ACLs dão a ele os mesmos direitos —, mas
    /// o rótulo faz o kernel recusar escrita em tudo o que for Medium.
    /// </summary>
    /// <summary>Containers rodam pela API do Windows; em outro sistema, um erro claro.</summary>
    public static void ExigirWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Executar containers requer Windows. Neste sistema funcionam build sem RUN, create, images, volumes e scale.");
        }
    }

    public static SafeKernelHandle CriarToken()
    {
        ExigirWindows();
        if (!NativeMetodosProcesso.OpenProcessToken(
                NativeMetodosProcesso.GetCurrentProcess(),
                NativeMetodosProcesso.TokenDuplicate | NativeMetodosProcesso.TokenQuery |
                NativeMetodosProcesso.TokenAssignPrimary | NativeMetodosProcesso.TokenAdjustDefault,
                out var atual))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel abrir o token do processo.");
        }

        using (atual)
        {
            if (!NativeMetodosProcesso.DuplicateTokenEx(
                    atual, NativeMetodosProcesso.MaximumAllowed, IntPtr.Zero,
                    NativeMetodosProcesso.SecurityImpersonation, NativeMetodosProcesso.TokenPrimary,
                    out var novo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel duplicar o token.");
            }

            try
            {
                RebaixarIntegridade(novo);
                return novo;
            }
            catch
            {
                novo.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Rotula a pasta (e, por herança, tudo dentro dela) como Low, liberando a
    /// escrita de um processo Low ali — ou, com <paramref name="somenteLeitura"/>,
    /// como Medium, garantindo que ele só leia, mesmo que a pasta tenha sido
    /// montada gravável antes. O <c>SetNamedSecurityInfo</c> propaga o rótulo
    /// para os arquivos que já existem. É o mesmo que
    /// <c>icacls PASTA /setintegritylevel (OI)(CI)low</c>.
    /// </summary>
    public static void RotularPasta(string pasta, bool somenteLeitura = false)
    {
        ExigirWindows();
        if (!NativeMetodosProcesso.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                somenteLeitura ? RotuloMedio : RotuloBaixo, 1, out var descritor, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Descritor de seguranca invalido.");
        }

        try
        {
            if (!NativeMetodosProcesso.GetSecurityDescriptorSacl(descritor, out _, out var sacl, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel ler o rotulo.");
            }

            var erro = NativeMetodosProcesso.SetNamedSecurityInfoW(
                pasta, NativeMetodosProcesso.SeFileObject, NativeMetodosProcesso.LabelSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (erro != 0)
            {
                throw new Win32Exception((int)erro,
                    $"Nao foi possivel rotular '{pasta}' com integridade baixa. Use --privileged para rodar sem confinamento.");
            }
        }
        finally
        {
            NativeMetodosProcesso.LocalFree(descritor);
        }
    }

    private static void RebaixarIntegridade(SafeKernelHandle token)
    {
        if (!NativeMetodosProcesso.ConvertStringSidToSidW(SidBaixa, out var sid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SID de integridade invalido.");
        }

        try
        {
            var rotulo = new TOKEN_MANDATORY_LABEL
            {
                Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = NativeMetodosProcesso.SeGroupIntegrity }
            };
            var tamanho = (uint)Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + NativeMetodosProcesso.GetLengthSid(sid);

            if (!NativeMetodosProcesso.SetTokenInformation(
                    token, NativeMetodosProcesso.TokenIntegrityLevel, ref rotulo, tamanho))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Nao foi possivel rebaixar a integridade do token.");
            }
        }
        finally
        {
            NativeMetodosProcesso.LocalFree(sid);
        }
    }
}
