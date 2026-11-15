using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

internal sealed record ResultadoExecucao(int CodigoSaida, ContabilidadeTrabalho Contabilidade);

/// <summary>
/// Lança o processo principal de um container dentro de um Job Object já
/// criado. Usado pelo start do container, pelo exec e pelos passos <c>RUN</c>
/// do build, que também rodam isolados.
///
/// Todo processo nasce assim:
/// <code>
/// CreateProcess(CREATE_SUSPENDED, token Low) → AssignProcessToJobObject → ResumeThread
/// </code>
/// </summary>
internal static class ExecutorProcesso
{
    private const uint AcessoLeitura = 0x80000000; // GENERIC_READ
    private const uint CompartilharTudo = 0x3;     // FILE_SHARE_READ | FILE_SHARE_WRITE
    private const uint AbrirExistente = 3;         // OPEN_EXISTING

    /// <summary>
    /// Handles herdáveis existem desde o <c>CreatePipe</c> até o pai fechar a
    /// sua cópia. Se outra thread criasse um processo nesse intervalo, ele
    /// herdaria a ponta de escrita do pipe e a leitura nunca chegaria ao fim.
    /// A trava serializa esse trecho — a classe <c>Process</c> faz o mesmo.
    /// </summary>
    private static readonly object TravaCriacao = new();

    static ExecutorProcesso()
    {
        // O cmd.exe escreve na página de código do console (850, 437...), que
        // o .NET só conhece com este provedor.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <param name="saida">
    /// Recebe cada linha de stdout/stderr. Com <c>null</c>, o processo herda o
    /// console de quem chamou — é o modo interativo (<c>-it</c>), sem log.
    /// </param>
    /// <param name="confinado">Roda com integridade baixa: só escreve no rootfs e nos volumes.</param>
    public static ResultadoExecucao Executar(
        ObjetoTrabalho trabalho,
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente,
        Action<string>? saida,
        bool confinado,
        Action<int>? aoIniciar = null)
    {
        SafeFileHandle? leitura = null;
        ProcessoNativo processo;

        using (var token = confinado ? IntegridadeBaixa.CriarToken() : null)
        {
            lock (TravaCriacao)
            {
                SafeFileHandle? escrita = null;
                SafeFileHandle? entrada = null;
                try
                {
                    if (saida is not null)
                    {
                        (leitura, escrita) = CriarPipe();
                        entrada = AbrirNul();
                    }

                    processo = Criar(comando, caminhoRootFs, ambiente, token, escrita, entrada);
                }
                catch
                {
                    leitura?.Dispose();
                    throw;
                }
                finally
                {
                    // O filho já tem a cópia dele. Se o pai mantivesse a ponta de
                    // escrita aberta, a leitura nunca receberia fim de arquivo.
                    escrita?.Dispose();
                    entrada?.Dispose();
                }
            }
        }

        using (processo)
        {
            Thread? leitor = null;
            try
            {
                trabalho.Atribuir(processo.Handle);
                aoIniciar?.Invoke(processo.Id);

                if (saida is not null)
                {
                    leitor = IniciarLeitura(leitura!, saida);
                }

                processo.Retomar();
            }
            catch
            {
                // Ainda suspenso: nunca executou nada. Encerrado aqui, não sobra.
                processo.Encerrar();
                leitura?.Dispose();
                throw;
            }

            var codigoSaida = processo.AguardarSaida();

            // Como no Docker, o container acaba quando o processo principal acaba.
            // Netos que ficaram para trás (um "start /b") são encerrados aqui; sem
            // isso eles segurariam o pipe de saída aberto e a leitura não terminaria.
            trabalho.EncerrarRestantes();
            var contabilidade = trabalho.Contabilizar();

            // Agora que ninguém mais segura o pipe, espera o fim da leitura para
            // não perder as últimas linhas.
            leitor?.Join();

            return new ResultadoExecucao(codigoSaida, contabilidade);
        }
    }

    /// <summary>
    /// Executa um processo adicional no grupo de um container vivo, ligado ao
    /// console de quem chamou. É o <c>docker exec</c>.
    /// </summary>
    public static int ExecutarAnexado(
        ObjetoTrabalho trabalho,
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente,
        bool confinado)
    {
        ProcessoNativo processo;
        using (var token = confinado ? IntegridadeBaixa.CriarToken() : null)
        {
            processo = Criar(comando, caminhoRootFs, ambiente, token, saida: null, entrada: null);
        }

        using (processo)
        {
            try
            {
                trabalho.Atribuir(processo.Handle);
                processo.Retomar();
            }
            catch
            {
                processo.Encerrar();
                throw;
            }

            return processo.AguardarSaida();
        }
    }

    private static ProcessoNativo Criar(
        string comando,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente,
        SafeKernelHandle? token,
        SafeFileHandle? saida,
        SafeFileHandle? entrada)
    {
        var raizWindows = RaizWindows();
        var cmd = Path.Combine(raizWindows, "System32", "cmd.exe");

        // /s /c "...": o cmd tira só o primeiro e o último caractere de aspas e
        // executa o resto como foi escrito, com aspas internas preservadas.
        var linhaComando = $"\"{cmd}\" /s /c \"{comando}\"";

        return ProcessoNativo.CriarSuspenso(
            cmd, linhaComando, caminhoRootFs, MontarAmbiente(raizWindows, caminhoRootFs, ambiente),
            token, saida, entrada);
    }

    /// <summary>
    /// Substitui o ambiente herdado do processo pai por um conjunto mínimo. Sem
    /// isto, toda variável do host — inclusive credenciais e caminhos pessoais —
    /// entraria no container. As variáveis de temporário apontam para o rootfs,
    /// então arquivos temporários do container ficam dentro dele. Por cima vêm
    /// as variáveis da imagem (<c>ENV</c>) e do <c>-e</c>.
    /// </summary>
    private static Dictionary<string, string> MontarAmbiente(
        string raizWindows,
        string caminhoRootFs,
        IReadOnlyDictionary<string, string> ambiente)
    {
        var resultado = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = raizWindows,
            ["ComSpec"] = Path.Combine(raizWindows, "System32", "cmd.exe"),
            ["Path"] = $"{Path.Combine(raizWindows, "System32")};{raizWindows}",
            ["TEMP"] = caminhoRootFs,
            ["TMP"] = caminhoRootFs
        };

        foreach (var (chave, valor) in ambiente)
        {
            resultado[chave] = valor;
        }

        return resultado;
    }

    private static string RaizWindows() =>
        Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    /// <summary>
    /// Um pipe só para stdout e stderr: as duas saídas chegam na ordem em que
    /// foram escritas. Só a ponta de escrita é herdável — é ela que vai para o
    /// container; a de leitura fica com o dono.
    /// </summary>
    private static (SafeFileHandle Leitura, SafeFileHandle Escrita) CriarPipe()
    {
        var atributos = AtributosHerdaveis();
        if (!NativeMetodosProcesso.CreatePipe(out var leitura, out var escrita, ref atributos, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel criar o pipe de saida.");
        }

        NativeMetodosProcesso.SetHandleInformation(leitura, NativeMetodosProcesso.HandleFlagInherit, 0);
        return (leitura, escrita);
    }

    /// <summary>O stdin do container em segundo plano é o dispositivo nulo, como um <c>&lt; nul</c>.</summary>
    private static SafeFileHandle AbrirNul()
    {
        var atributos = AtributosHerdaveis();
        var handle = NativeMetodosProcesso.CreateFileW(
            "NUL", AcessoLeitura, CompartilharTudo, ref atributos, AbrirExistente, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel abrir NUL.");
        }

        return handle;
    }

    private static SECURITY_ATTRIBUTES AtributosHerdaveis() => new()
    {
        nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
        bInheritHandle = 1
    };

    private static Thread IniciarLeitura(SafeFileHandle leitura, Action<string> saida)
    {
        var leitor = new Thread(() =>
        {
            using var fluxo = new FileStream(leitura, FileAccess.Read, 4096, isAsync: false);
            using var texto = new StreamReader(fluxo, CodificacaoConsole());

            // ReadLine devolve null quando todas as pontas de escrita fecharam.
            while (texto.ReadLine() is { } linha)
            {
                saida(linha);
            }
        })
        {
            IsBackground = true,
            Name = "minidocker-saida"
        };

        leitor.Start();
        return leitor;
    }

    private static Encoding CodificacaoConsole()
    {
        var pagina = NativeMetodosProcesso.GetConsoleOutputCP();
        if (pagina == 0)
        {
            // O shim não tem console; o cmd.exe usa então a página OEM do sistema.
            pagina = NativeMetodosProcesso.GetOEMCP();
        }

        try
        {
            return Encoding.GetEncoding((int)pagina);
        }
        catch (Exception excecao) when (excecao is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }
}
