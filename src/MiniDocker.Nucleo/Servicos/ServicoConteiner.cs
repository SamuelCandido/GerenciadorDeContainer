using System.Text.RegularExpressions;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Repositorios;

namespace MiniDocker.Nucleo.Servicos;

public partial class ServicoConteiner : IServicoConteiner
{
    /// <summary>Código de saída de um container parado, o mesmo do Docker (128 + SIGKILL).</summary>
    public const int CodigoSaidaParado = 137;

    private static readonly TimeSpan EsperaMaximaParada = TimeSpan.FromSeconds(10);

    private readonly IRepositorioConteiner repositorio;
    private readonly IServicoImagem imagens;
    private readonly string raizWorkspace;

    public ServicoConteiner(string? raizWorkspace = null)
        : this(new RepositorioConteinerArquivo(raizWorkspace), new ServicoImagem(raizWorkspace), raizWorkspace)
    {
    }

    public ServicoConteiner(IRepositorioConteiner repositorio, IServicoImagem imagens, string? raizWorkspace = null)
    {
        this.repositorio = repositorio;
        this.imagens = imagens;
        this.raizWorkspace = Workspace.Raiz(raizWorkspace);
        Directory.CreateDirectory(Path.Combine(this.raizWorkspace, "containers"));
        Directory.CreateDirectory(Path.Combine(this.raizWorkspace, "logs"));
    }

    public Conteiner Criar(string? nome, string imagem, string? comando = null, OpcoesConteiner? opcoes = null)
    {
        opcoes ??= OpcoesConteiner.Padrao;

        // Tudo validado antes de criar diretório ou registro, para que um erro
        // não deixe um container pela metade no repositório.
        nome ??= GerarNome();
        ValidarNome(nome);
        opcoes.Limites.Validar();
        var origem = imagens.Obter(imagem);

        comando = string.IsNullOrWhiteSpace(comando) ? origem.Comando : comando;
        if (string.IsNullOrWhiteSpace(comando))
        {
            throw new ArgumentException(
                $"Nenhum comando informado e a imagem '{origem.Referencia}' nao define CMD.", nameof(comando));
        }

        if (repositorio.ObterPorNome(nome) is not null)
        {
            throw new InvalidOperationException($"Ja existe um container com o nome '{nome}'.");
        }

        var caminhoRootFs = Path.Combine(raizWorkspace, "containers", nome);
        Workspace.ApagarDiretorio(caminhoRootFs);
        if (origem.EhScratch)
        {
            Directory.CreateDirectory(caminhoRootFs);
        }
        else
        {
            // Cópia, não referência: o container altera o próprio rootfs sem
            // tocar na imagem nem nos outros containers criados dela.
            Workspace.CopiarDiretorio(origem.CaminhoRootFs, caminhoRootFs);
        }

        var ambiente = new Dictionary<string, string>(origem.Ambiente);
        foreach (var (chave, valor) in opcoes.Ambiente)
        {
            ambiente[chave] = valor;
        }

        var conteiner = new Conteiner
        {
            Nome = nome,
            Imagem = origem.Referencia,
            Comando = comando,
            Ambiente = ambiente,
            CaminhoRootFs = caminhoRootFs,
            Limites = opcoes.Limites,
            RemoverAoFinalizar = opcoes.RemoverAoFinalizar
        };
        repositorio.Salvar(conteiner);
        return conteiner;
    }

    public Conteiner Iniciar(string nomeOuId, Action<string>? saida = null, bool interativo = false)
    {
        var conteiner = Obter(nomeOuId);
        if (conteiner.Estado == EstadoConteiner.Executando)
        {
            throw new InvalidOperationException($"O container '{conteiner.Nome}' ja esta em execucao.");
        }

        int? codigoSaida = null;
        ContabilidadeTrabalho? contabilidade = null;

        // O Job Object nasce antes de o registro dizer "Executando". Assim,
        // quem vir Executando sempre encontra o job no kernel — e, se não
        // encontrar, sabe que o dono morreu. O nome do job também impede dois
        // starts simultâneos do mesmo container.
        using (var trabalho = new ObjetoTrabalho(conteiner.Limites, conteiner.NomeTrabalho))
        {
            conteiner = repositorio.Atualizar(conteiner.Nome, c =>
            {
                c.Estado = EstadoConteiner.Executando;
                c.IniciadoEm = DateTime.UtcNow;
                c.FinalizadoEm = null;
                c.CodigoSaida = null;
                c.IdProcesso = null;
                c.ParadaSolicitada = false;
            });

            using var log = interativo ? null : AbrirLog(conteiner);
            try
            {
                Action<string>? encaminhar = log is null ? null : linha =>
                {
                    log.WriteLine(linha);
                    saida?.Invoke(linha);
                };

                var resultado = ExecutorProcesso.Executar(
                    trabalho, conteiner.Comando, conteiner.CaminhoRootFs, conteiner.Ambiente, encaminhar,
                    aoIniciar: pid => repositorio.Atualizar(conteiner.Nome, c => c.IdProcesso = pid));

                codigoSaida = resultado.CodigoSaida;
                contabilidade = resultado.Contabilidade;
            }
            catch (Exception excecao)
            {
                log?.WriteLine($"[minidocker] erro ao executar: {excecao.Message}");
                throw;
            }
            finally
            {
                // Mesmo se a execução falhar no meio, o registro não pode ficar
                // preso em Executando. CodigoSaida nulo indica falha. Isto roda
                // com o job ainda aberto, então ninguém observa um meio-termo.
                conteiner = repositorio.Atualizar(conteiner.Nome, c =>
                {
                    c.Estado = c.ParadaSolicitada ? EstadoConteiner.Parado : EstadoConteiner.Finalizado;
                    c.FinalizadoEm = DateTime.UtcNow;
                    c.CodigoSaida = codigoSaida;
                    if (contabilidade is not null)
                    {
                        c.MemoriaUsadaBytes = contabilidade.PicoMemoriaBytes;
                        c.TempoCpuMs = contabilidade.TempoCpuMs;
                        c.TotalProcessos = contabilidade.TotalProcessos;
                        c.FalhasDePagina = contabilidade.FalhasDePagina;
                    }
                });
            }
        }

        if (conteiner.RemoverAoFinalizar)
        {
            Remover(conteiner.Nome);
        }

        return conteiner;
    }

    public Conteiner Executar(string? nome, string imagem, string? comando = null,
        OpcoesConteiner? opcoes = null, Action<string>? saida = null)
    {
        var conteiner = Criar(nome, imagem, comando, opcoes);
        return Iniciar(conteiner.Nome, saida);
    }

    public Conteiner Obter(string nomeOuId)
    {
        var todos = repositorio.ObterTodos();
        var conteiner = todos.FirstOrDefault(c => c.Nome == nomeOuId);

        if (conteiner is null)
        {
            var porId = todos
                .Where(c => c.Id.StartsWith(nomeOuId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            conteiner = porId.Count switch
            {
                1 => porId[0],
                0 => throw new InvalidOperationException($"Container '{nomeOuId}' nao existe."),
                _ => throw new InvalidOperationException(
                    $"O prefixo '{nomeOuId}' corresponde a mais de um container.")
            };
        }

        return Reconciliar(conteiner);
    }

    public IReadOnlyList<Conteiner> Listar() =>
        repositorio.ObterTodos().Select(Reconciliar).ToList();

    public Conteiner Parar(string nomeOuId)
    {
        var conteiner = Obter(nomeOuId);
        if (conteiner.Estado != EstadoConteiner.Executando)
        {
            throw new InvalidOperationException($"O container '{conteiner.Nome}' nao esta em execucao.");
        }

        // A marca vem antes do TerminateJobObject: quando o processo principal
        // morrer, o dono já vai ler que foi um stop, e não um término normal.
        repositorio.Atualizar(conteiner.Nome, c => c.ParadaSolicitada = true);

        using (var trabalho = ObjetoTrabalho.Abrir(conteiner.NomeTrabalho))
        {
            trabalho?.Encerrar(CodigoSaidaParado);
        }

        // O registro final é escrito pelo dono, em outro processo. Espera por
        // ele como o docker stop espera o container sair.
        var limite = DateTime.UtcNow + EsperaMaximaParada;
        while (DateTime.UtcNow < limite)
        {
            if (repositorio.ObterPorNome(conteiner.Nome) is not { } atual)
            {
                // Container com --rm: o dono já o removeu ao terminar.
                conteiner.Estado = EstadoConteiner.Parado;
                conteiner.CodigoSaida = CodigoSaidaParado;
                return conteiner;
            }

            conteiner = Reconciliar(atual);
            if (conteiner.Estado != EstadoConteiner.Executando)
            {
                return conteiner;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"O container '{conteiner.Nome}' nao terminou a tempo.");
    }

    public void Remover(string nomeOuId, bool forcar = false)
    {
        var conteiner = Obter(nomeOuId);

        if (conteiner.Estado == EstadoConteiner.Executando)
        {
            if (!forcar)
            {
                throw new InvalidOperationException(
                    $"O container '{conteiner.Nome}' esta em execucao. Pare-o antes ou use rm -f.");
            }

            // Um --rm faria o dono remover sozinho ao terminar; aqui a remoção
            // é explícita, então o dono não deve tentar de novo.
            repositorio.Atualizar(conteiner.Nome, c => c.RemoverAoFinalizar = false);
            Parar(conteiner.Nome);
        }

        repositorio.Remover(conteiner.Nome);
        Workspace.ApagarDiretorio(conteiner.CaminhoRootFs);
        File.Delete(CaminhoLog(conteiner));
    }

    public int ExecutarNoConteiner(string nomeOuId, string comando)
    {
        var conteiner = Obter(nomeOuId);
        if (string.IsNullOrWhiteSpace(comando))
        {
            throw new ArgumentException("O comando nao pode ser vazio.", nameof(comando));
        }

        using var trabalho = AbrirTrabalho(conteiner);

        // Entra no mesmo Job Object: herda os limites, as restrições de
        // interface e é encerrado junto com o container.
        return ExecutorProcesso.ExecutarAnexado(trabalho, comando, conteiner.CaminhoRootFs, conteiner.Ambiente);
    }

    public ContabilidadeTrabalho Estatisticas(string nomeOuId)
    {
        var conteiner = Obter(nomeOuId);
        using var trabalho = AbrirTrabalho(conteiner);
        return trabalho.Contabilizar();
    }

    public void LerLogs(string nomeOuId, bool seguir, Action<string> saida, CancellationToken cancelamento = default)
    {
        var conteiner = Obter(nomeOuId);
        var caminho = CaminhoLog(conteiner);
        if (!File.Exists(caminho))
        {
            return;
        }

        using var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var leitor = new StreamReader(fluxo);

        while (true)
        {
            // O estado é lido antes de esvaziar o arquivo: se o container já
            // tinha terminado, tudo o que ele escreveu já está no log.
            var rodando = seguir && Obter(conteiner.Nome).Estado == EstadoConteiner.Executando;

            while (leitor.ReadLine() is { } linha)
            {
                saida(linha);
            }

            if (!rodando || cancelamento.WaitHandle.WaitOne(200))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Corrige um registro que diz Executando mas cujo Job Object não existe
    /// mais — o processo dono morreu sem gravar o fim (foi morto, a máquina
    /// caiu). Com KILL_ON_JOB_CLOSE, o grupo morreu junto com ele.
    /// </summary>
    private Conteiner Reconciliar(Conteiner conteiner)
    {
        if (conteiner.Estado != EstadoConteiner.Executando || ObjetoTrabalho.Existe(conteiner.NomeTrabalho))
        {
            return conteiner;
        }

        return repositorio.Atualizar(conteiner.Nome, c =>
        {
            // Reconfere dentro da trava: o dono pode ter acabado de gravar.
            if (c.Estado == EstadoConteiner.Executando)
            {
                c.Estado = c.ParadaSolicitada ? EstadoConteiner.Parado : EstadoConteiner.Finalizado;
                c.FinalizadoEm = DateTime.UtcNow;
            }
        });
    }

    private static ObjetoTrabalho AbrirTrabalho(Conteiner conteiner) =>
        (conteiner.Estado == EstadoConteiner.Executando ? ObjetoTrabalho.Abrir(conteiner.NomeTrabalho) : null)
            ?? throw new InvalidOperationException($"O container '{conteiner.Nome}' nao esta em execucao.");

    private StreamWriter AbrirLog(Conteiner conteiner)
    {
        // Append, como o Docker: um restart continua o mesmo log.
        var fluxo = new FileStream(CaminhoLog(conteiner), FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(fluxo) { AutoFlush = true };
    }

    private string CaminhoLog(Conteiner conteiner) =>
        Path.Combine(raizWorkspace, "logs", $"{conteiner.Id}.log");

    /// <summary>
    /// O nome vira nome de diretório, então precisa ser restrito: sem isso um
    /// nome como <c>..\..\Windows</c> faria o rootfs, e o rm, sair do workspace.
    /// </summary>
    private static void ValidarNome(string nome)
    {
        if (!PadraoNome().IsMatch(nome))
        {
            throw new ArgumentException(
                $"Nome de container invalido: '{nome}'. Use letras, digitos, '_', '.' ou '-'.", nameof(nome));
        }
    }

    private string GerarNome()
    {
        string[] adjetivos = ["agil", "bravo", "calmo", "curioso", "esperto", "feliz", "gentil", "sereno", "veloz", "zen"];
        string[] cientistas = ["dijkstra", "hopper", "knuth", "lamport", "liskov", "lovelace", "ritchie", "tanenbaum", "thompson", "turing"];

        for (var tentativa = 0; ; tentativa++)
        {
            var nome = $"{adjetivos[Random.Shared.Next(adjetivos.Length)]}_{cientistas[Random.Shared.Next(cientistas.Length)]}";
            if (tentativa >= 20)
            {
                nome += Random.Shared.Next(100);
            }

            if (repositorio.ObterPorNome(nome) is null)
            {
                return nome;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex PadraoNome();
}
