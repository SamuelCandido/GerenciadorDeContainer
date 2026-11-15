using System.Text.RegularExpressions;
using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Rede;
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
    private readonly ServicoVolume volumes;
    private readonly RegistroEventos eventos;
    private readonly string raizWorkspace;

    public ServicoConteiner(string? raizWorkspace = null)
        : this(new RepositorioConteinerArquivo(raizWorkspace), new ServicoImagem(raizWorkspace), raizWorkspace)
    {
    }

    public ServicoConteiner(IRepositorioConteiner repositorio, IServicoImagem imagens, string? raizWorkspace = null)
    {
        this.repositorio = repositorio;
        this.imagens = imagens;
        volumes = new ServicoVolume(raizWorkspace, repositorio);
        eventos = new RegistroEventos(raizWorkspace);
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

        var especificacoes = opcoes.Volumes.Select(EspecificacaoVolume.Separar).ToList();
        foreach (var especificacao in especificacoes.Where(e => EspecificacaoVolume.EhNomeado(e.Origem)))
        {
            EspecificacaoVolume.ValidarNome(especificacao.Origem);
        }

        if (opcoes.RemoverAoFinalizar && opcoes.Reinicio.Modo != ModoReinicio.Nao)
        {
            throw new ArgumentException("--rm e --restart nao podem ser usados juntos.");
        }

        var portas = opcoes.Portas.Select(MapeamentoPorta.Interpretar).ToList();
        if (portas.GroupBy(p => p.PortaContainer).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A mesma porta do container foi publicada mais de uma vez.");
        }

        if (portas.Where(p => !p.Aleatoria).GroupBy(p => p.PortaHost).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A mesma porta do host foi usada mais de uma vez.");
        }

        if (opcoes.Privilegiado && especificacoes.Any(e => e.SomenteLeitura))
        {
            // O :ro é imposto pelo nível de integridade; sem confinamento, não haveria quem impusesse.
            throw new ArgumentException("Volumes :ro exigem o container confinado; nao use --privileged junto.");
        }

        var caminhoRootFs = Path.Combine(raizWorkspace, "containers", nome);
        Workspace.ApagarDiretorio(caminhoRootFs);
        List<Montagem> montagens;
        try
        {
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

            montagens = especificacoes
                .Select(e => Montar(caminhoRootFs, e.Origem, e.Destino) with { SomenteLeitura = e.SomenteLeitura })
                .ToList();
        }
        catch
        {
            Workspace.ApagarDiretorio(caminhoRootFs);
            throw;
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
            RemoverAoFinalizar = opcoes.RemoverAoFinalizar,
            Volumes = montagens,
            Privilegiado = opcoes.Privilegiado,
            Reinicio = opcoes.Reinicio,
            Grupo = opcoes.Grupo,
            Projeto = opcoes.Projeto,
            Portas = portas
        };
        repositorio.Salvar(conteiner);
        eventos.Registrar(nome, TiposEvento.Criado, $"imagem {origem.Referencia}");
        return conteiner;
    }

    public IReadOnlyList<Conteiner> ListarGrupo(string grupo) =>
        repositorio.ObterTodos()
            .Where(c => c.Grupo == grupo)
            .OrderBy(c => IndiceReplica(c.Nome))
            .ToList();

    /// <summary>
    /// Ajusta o grupo para <paramref name="quantidade"/> réplicas, como o
    /// <c>docker compose up --scale</c>. As novas copiam a configuração da
    /// primeira (imagem, comando, ambiente, limites, volumes, restart) e são
    /// devolvidas ainda em <c>Criado</c>, para quem chamou iniciá-las. As
    /// excedentes, as de número mais alto, são paradas e removidas.
    /// </summary>
    public IReadOnlyList<Conteiner> Escalar(string grupo, int quantidade)
    {
        if (quantidade < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantidade), "A quantidade de replicas nao pode ser negativa.");
        }

        var membros = ListarGrupo(grupo);
        if (membros.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nao ha replicas do grupo '{grupo}'. Crie com: minidocker run -d --replicas N --name {grupo} IMAGEM");
        }

        var modelo = membros[0];
        ValidarPortasDoGrupo(modelo.Portas, quantidade);

        foreach (var excedente in membros.Skip(quantidade).Reverse())
        {
            Remover(excedente.Nome, forcar: true);
        }

        var opcoes = new OpcoesConteiner
        {
            Ambiente = modelo.Ambiente,
            Limites = modelo.Limites,
            Volumes = modelo.Volumes.Select(EspecificarDeNovo).ToList(),
            Privilegiado = modelo.Privilegiado,
            Reinicio = modelo.Reinicio,
            Grupo = grupo,
            Projeto = modelo.Projeto,
            Portas = modelo.Portas.Select(p => $"{p.Ip}:{p.PortaHost}:{p.PortaContainer}").ToList()
        };

        var novas = new List<Conteiner>();
        var proximo = membros.Max(c => IndiceReplica(c.Nome)) + 1;
        for (var restantes = quantidade - membros.Count; restantes > 0; restantes--)
        {
            novas.Add(Criar(NomeReplica(grupo, proximo++), modelo.Imagem, modelo.Comando, opcoes));
        }

        return novas;
    }

    public static string NomeReplica(string grupo, int indice) => $"{grupo}-{indice}";

    /// <summary>
    /// Duas réplicas não podem escutar na mesma porta do host. Com mais de uma,
    /// cada porta publicada precisa ser sorteada (<c>-p 80</c>), como no Docker.
    /// </summary>
    public static void ValidarPortasDoGrupo(IEnumerable<MapeamentoPorta> portas, int quantidade)
    {
        if (quantidade > 1 && portas.FirstOrDefault(p => !p.Aleatoria) is { } fixa)
        {
            throw new ArgumentException(
                $"O grupo publica a porta fixa {fixa.PortaHost}, que so uma replica pode ocupar. " +
                $"Para varias replicas, publique com porta sorteada: -p {fixa.PortaContainer}");
        }
    }

    private static int IndiceReplica(string nome) =>
        int.TryParse(nome[(nome.LastIndexOf('-') + 1)..], out var indice) ? indice : 0;

    private static string EspecificarDeNovo(Montagem montagem) =>
        $"{montagem.NomeVolume ?? montagem.Origem}:{montagem.Destino}{(montagem.SomenteLeitura ? ":ro" : "")}";

    /// <summary>
    /// Monta um volume: uma junction em <c>rootfs\destino</c> apontando para a
    /// pasta do host ou do volume nomeado. Como no Docker, um volume nomeado
    /// vazio recebe o que a imagem tinha naquele caminho; uma pasta do host
    /// esconde o conteúdo da imagem.
    /// </summary>
    private Montagem Montar(string caminhoRootFs, string origem, string destino)
    {
        var nomeado = EspecificacaoVolume.EhNomeado(origem);
        var pastaOrigem = nomeado ? volumes.Criar(origem) : Path.GetFullPath(origem);
        Directory.CreateDirectory(pastaOrigem);

        var pontoMontagem = Workspace.CaminhoDentro(caminhoRootFs, destino);
        if (Path.GetRelativePath(caminhoRootFs, pontoMontagem) == ".")
        {
            throw new ArgumentException("Um volume nao pode ser montado na raiz do container.");
        }

        if (File.Exists(pontoMontagem) || Workspace.EhLink(pontoMontagem))
        {
            throw new ArgumentException($"O destino '{destino}' ja e um arquivo ou outro volume no container.");
        }

        if (Directory.Exists(pontoMontagem))
        {
            if (nomeado && Workspace.DiretorioVazio(pastaOrigem))
            {
                Workspace.CopiarDiretorio(pontoMontagem, pastaOrigem);
            }

            Workspace.ApagarDiretorio(pontoMontagem);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(pontoMontagem)!);
        Workspace.CriarJuncao(pontoMontagem, pastaOrigem);

        return new Montagem
        {
            Origem = pastaOrigem,
            Destino = Path.GetRelativePath(caminhoRootFs, pontoMontagem),
            NomeVolume = nomeado ? origem : null
        };
    }

    /// <summary>
    /// Rotula como Low o que o container pode escrever: o rootfs e os volumes.
    /// O resto do disco continua Medium, e o processo, em Low, não escreve lá.
    /// Repetido a cada início, porque uma pasta de volume pode ter sido recriada.
    /// </summary>
    /// <summary>
    /// O <c>:ro</c> e o gravável são um rótulo <b>na pasta</b>, um só para todos
    /// os processos. A mesma pasta (ou uma dentro da outra) não pode ser só
    /// leitura para um container e gravável para outro ao mesmo tempo: o
    /// segundo a subir trocaria o rótulo do primeiro.
    /// </summary>
    public static void VerificarConflitoDeVolumes(Conteiner conteiner, IEnumerable<Conteiner> rodando)
    {
        if (conteiner.Privilegiado)
        {
            return;
        }

        foreach (var outro in rodando.Where(c => !c.Privilegiado && c.Nome != conteiner.Nome))
        {
            foreach (var meu in conteiner.Volumes)
            {
                if (outro.Volumes.FirstOrDefault(v => v.SomenteLeitura != meu.SomenteLeitura
                        && Sobrepoe(v.Origem, meu.Origem)) is { } dele)
                {
                    throw new InvalidOperationException(
                        $"A pasta '{meu.Origem}' esta montada {(dele.SomenteLeitura ? "so leitura" : "gravavel")} " +
                        $"no container '{outro.Nome}'. O :ro e um rotulo de integridade na propria pasta, entao " +
                        "ela nao pode ser so leitura num container e gravavel em outro ao mesmo tempo. Monte do mesmo jeito nos dois.");
                }
            }
        }
    }

    private static bool Sobrepoe(string a, string b)
    {
        var comparacao = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)) + Path.DirectorySeparatorChar;
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, comparacao) || b.StartsWith(a, comparacao);
    }

    private static void PrepararConfinamento(Conteiner conteiner)
    {
        if (conteiner.Privilegiado)
        {
            return;
        }

        IntegridadeBaixa.RotularPasta(conteiner.CaminhoRootFs);

        // Um volume :ro é rotulado Medium, o padrão do disco: o container lê,
        // mas a regra no-write-up recusa a escrita. Explícito, e não só "sem
        // rótulo", porque a pasta pode ter sido montada gravável (Low) antes.
        foreach (var volume in conteiner.Volumes)
        {
            IntegridadeBaixa.RotularPasta(volume.Origem, volume.SomenteLeitura);
        }
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
        string? erroKernel = null;
        var reinicios = 0;
        var nome = conteiner.Nome;
        IntegridadeBaixa.ExigirWindows();
        VerificarConflitoDeVolumes(conteiner, Listar().Where(c => c.Estado == EstadoConteiner.Executando));
        PrepararConfinamento(conteiner);

        // As portas são abertas antes de tudo: se a do host estiver ocupada, o
        // start falha sem mudar o estado. Elas vivem neste processo (o dono) e
        // fecham junto com o container.
        using (var rede = RedeConteiner.Abrir(conteiner.Portas))
        // O Job Object nasce antes de o registro dizer "Executando". Assim,
        // quem vir Executando sempre encontra o job no kernel — e, se não
        // encontrar, sabe que o dono morreu. O nome do job também impede dois
        // starts simultâneos do mesmo container.
        using (var trabalho = new ObjetoTrabalho(conteiner.Limites, conteiner.NomeTrabalho))
        {
            using var log = interativo ? null : AbrirLog(conteiner);
            var travaLog = new object();

            void Escrever(string linha)
            {
                lock (travaLog)
                {
                    // Cada linha leva o horário: é o que permite intercalar os
                    // logs de várias réplicas na ordem em que aconteceram.
                    log?.WriteLine(LinhaLog.Formatar(DateTime.UtcNow, linha));
                }

                saida?.Invoke(linha);
            }

            // O kernel avisa os erros do grupo pela porta de conclusão: o
            // container não precisa ser consultado para se saber que algo deu errado.
            var monitor = trabalho.MonitorarEventos(evento =>
            {
                if (!evento.EhErro)
                {
                    return;
                }

                Volatile.Write(ref erroKernel, evento.Descricao);
                eventos.Registrar(nome, TiposEvento.Erro, evento.Descricao);
                Escrever($"[minidocker] kernel: {evento.Descricao}");
            });

            conteiner = repositorio.Atualizar(nome, c =>
            {
                c.Estado = EstadoConteiner.Executando;
                c.IniciadoEm = DateTime.UtcNow;
                c.FinalizadoEm = null;
                c.CodigoSaida = null;
                c.IdProcesso = null;
                c.ParadaSolicitada = false;
                c.Reinicios = 0;
                c.MotivoTermino = null;
                c.Portas = rede.Mapeamentos.ToList();
            });
            eventos.Registrar(nome, TiposEvento.Iniciado,
                rede.Mapeamentos.Count == 0 ? conteiner.Comando
                    : $"{conteiner.Comando} (portas {string.Join(", ", rede.Mapeamentos.Select(p => p.Descrever()))})");

            foreach (var porta in rede.Mapeamentos)
            {
                Escrever($"[minidocker] rede: {porta.Descrever()} (o programa escuta em 127.0.0.1:{porta.PortaInterna}, variavel PORT_{porta.PortaContainer})");
            }

            try
            {
                Action<string>? encaminhar = log is null ? null : Escrever;

                while (true)
                {
                    Volatile.Write(ref erroKernel, null);
                    var resultado = ExecutorProcesso.Executar(
                        trabalho, conteiner.Comando, conteiner.CaminhoRootFs, AmbienteDoProcesso(conteiner, rede),
                        encaminhar, confinado: !conteiner.Privilegiado,
                        aoIniciar: pid => repositorio.Atualizar(nome, c => c.IdProcesso = pid));

                    codigoSaida = resultado.CodigoSaida;
                    contabilidade = resultado.Contabilidade;

                    // Política de restart: o mesmo Job Object, um processo novo.
                    // Um stop desliga o reinício.
                    if (ParadaSolicitada(nome)
                        || !conteiner.Reinicio.DeveReiniciar(resultado.CodigoSaida, reinicios))
                    {
                        break;
                    }

                    var espera = PoliticaReinicio.Espera(reinicios);
                    reinicios++;
                    var motivo = DescreverTermino(resultado.CodigoSaida, Volatile.Read(ref erroKernel), parado: false)
                        ?? $"codigo {resultado.CodigoSaida}";
                    var tentativa = reinicios;
                    Escrever($"[minidocker] processo terminou ({motivo}); reiniciando em {espera.TotalSeconds:0} s (reinicio {tentativa})");
                    eventos.Registrar(nome, TiposEvento.Reiniciando, $"reinicio {tentativa} apos {motivo}");
                    repositorio.Atualizar(nome, c =>
                    {
                        c.Reinicios = tentativa;
                        c.MotivoTermino = motivo;
                    });

                    if (AguardarOuParar(nome, espera))
                    {
                        break;
                    }
                }
            }
            catch (Exception excecao)
            {
                Escrever($"[minidocker] erro ao executar: {excecao.Message}");
                throw;
            }
            finally
            {
                // A fila da porta é FIFO: ao fechar o monitor, os eventos que o
                // kernel já postou foram tratados, e o motivo abaixo os considera.
                monitor.Dispose();

                // Mesmo se a execução falhar no meio, o registro não pode ficar
                // preso em Executando. CodigoSaida nulo indica falha. Isto roda
                // com o job ainda aberto, então ninguém observa um meio-termo.
                conteiner = repositorio.Atualizar(nome, c =>
                {
                    c.Estado = c.ParadaSolicitada ? EstadoConteiner.Parado : EstadoConteiner.Finalizado;
                    c.FinalizadoEm = DateTime.UtcNow;
                    c.CodigoSaida = c.ParadaSolicitada ? CodigoSaidaParado : codigoSaida;
                    c.MotivoTermino = DescreverTermino(codigoSaida, erroKernel, c.ParadaSolicitada);
                    if (contabilidade is not null)
                    {
                        c.MemoriaUsadaBytes = contabilidade.PicoMemoriaBytes;
                        c.TempoCpuMs = contabilidade.TempoCpuMs;
                        c.TotalProcessos = contabilidade.TotalProcessos;
                        c.FalhasDePagina = contabilidade.FalhasDePagina;
                    }
                });

                RegistrarFim(conteiner);
            }
        }

        if (conteiner.RemoverAoFinalizar)
        {
            Remover(conteiner.Nome);
        }

        return conteiner;
    }

    private void RegistrarFim(Conteiner conteiner)
    {
        var codigo = CodigosSaida.Descrever(conteiner.CodigoSaida);
        var (tipo, detalhe) = conteiner.Estado == EstadoConteiner.Parado
            ? (TiposEvento.Parado, codigo)
            : conteiner.CodigoSaida == 0 && conteiner.MotivoTermino is null
                ? (TiposEvento.Terminou, codigo)
                : (TiposEvento.Falhou, conteiner.MotivoTermino ?? codigo);
        eventos.Registrar(conteiner.Nome, tipo, detalhe);
    }

    /// <summary>
    /// Por que o container terminou, em português, ou <c>null</c> num término
    /// normal. O erro do kernel tem prioridade: "limite de memória atingido"
    /// explica mais do que o código 1 que o programa devolveu em seguida.
    /// </summary>
    public static string? DescreverTermino(int? codigo, string? erroKernel, bool parado)
    {
        if (parado)
        {
            return "parado pelo usuario (stop)";
        }

        if (erroKernel is not null)
        {
            return erroKernel;
        }

        return codigo switch
        {
            null => "falha ao executar",
            0 => null,
            var c when CodigosSaida.EhFalha(c.Value) => $"falha: {CodigosSaida.Descrever(c)}",
            _ => $"terminou com erro (codigo {codigo})"
        };
    }

    /// <summary>
    /// O ambiente do processo: o do container mais <c>HOSTNAME</c> com o nome
    /// dele, como no Docker. Com réplicas, é o que diz a cada uma quem ela é.
    /// </summary>
    private static Dictionary<string, string> AmbienteDoProcesso(Conteiner conteiner, RedeConteiner? rede = null)
    {
        var ambiente = new Dictionary<string, string>(conteiner.Ambiente);
        ambiente.TryAdd("HOSTNAME", conteiner.Nome);

        // A porta em que o programa deve escutar. PORT é a da primeira porta
        // publicada; PORT_80 etc. existem para quem publica mais de uma.
        var portas = (rede?.Mapeamentos ?? conteiner.Portas).Where(p => p.PortaInterna is not null).ToList();
        foreach (var porta in portas)
        {
            ambiente[$"PORT_{porta.PortaContainer}"] = porta.PortaInterna!.Value.ToString();
        }

        if (portas.Count > 0)
        {
            ambiente["PORT"] = portas[0].PortaInterna!.Value.ToString();
        }

        return ambiente;
    }

    private bool ParadaSolicitada(string nome) =>
        repositorio.ObterPorNome(nome) is not { ParadaSolicitada: false };

    /// <summary>Espera o backoff do restart, acordando cedo se vier um stop.</summary>
    private bool AguardarOuParar(string nome, TimeSpan espera)
    {
        var limite = DateTime.UtcNow + espera;
        while (DateTime.UtcNow < limite)
        {
            if (ParadaSolicitada(nome))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return ParadaSolicitada(nome);
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

        // Os volumes são junctions dentro do rootfs. ApagarDiretorio remove a
        // junction sem entrar nela: os dados do volume ficam intactos.
        Workspace.ApagarDiretorio(conteiner.CaminhoRootFs);
        File.Delete(CaminhoLog(conteiner));
        eventos.Registrar(conteiner.Nome, TiposEvento.Removido);
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
        return ExecutorProcesso.ExecutarAnexado(
            trabalho, comando, conteiner.CaminhoRootFs, AmbienteDoProcesso(conteiner), confinado: !conteiner.Privilegiado);
    }

    public ContabilidadeTrabalho Estatisticas(string nomeOuId)
    {
        var conteiner = Obter(nomeOuId);
        using var trabalho = AbrirTrabalho(conteiner);
        return trabalho.Contabilizar();
    }

    public void LerLogs(string nomeOuId, bool seguir, Action<string> saida, CancellationToken cancelamento = default) =>
        LerLogs([nomeOuId], seguir, linha => saida(linha.Texto), cancelamento);

    /// <summary>
    /// Lê os logs de vários containers de uma vez — as réplicas de um grupo,
    /// os serviços de um compose — intercalando as linhas pelo horário em que
    /// foram gravadas.
    /// </summary>
    public void LerLogs(IReadOnlyList<string> nomesOuIds, bool seguir, Action<LinhaLog> saida,
        CancellationToken cancelamento = default)
    {
        var conteineres = nomesOuIds.Select(Obter).ToList();
        var leitores = conteineres.Select(c => new LeitorLog(c.Nome, CaminhoLog(c))).ToList();

        try
        {
            while (true)
            {
                // O estado é lido antes de esvaziar os arquivos: se os containers
                // já tinham terminado, tudo o que escreveram já está nos logs.
                var rodando = seguir && conteineres.Any(EstaRodando);

                // OrderBy é estável: linhas sem horário (logs antigos) mantêm a ordem do arquivo.
                foreach (var linha in leitores.SelectMany(l => l.LerNovas()).OrderBy(l => l.Momento ?? DateTime.MinValue))
                {
                    saida(linha);
                }

                if (!rodando || cancelamento.WaitHandle.WaitOne(200))
                {
                    return;
                }
            }
        }
        finally
        {
            leitores.ForEach(l => l.Dispose());
        }
    }

    private bool EstaRodando(Conteiner conteiner) =>
        repositorio.ObterPorNome(conteiner.Nome) is { } atual && Reconciliar(atual).Estado == EstadoConteiner.Executando;

    /// <summary>Lê o log de um container aos poucos, de onde parou.</summary>
    private sealed class LeitorLog(string conteiner, string caminho) : IDisposable
    {
        private StreamReader? leitor;

        public List<LinhaLog> LerNovas()
        {
            var linhas = new List<LinhaLog>();
            if (leitor is null)
            {
                if (!File.Exists(caminho))
                {
                    return linhas;
                }

                leitor = new StreamReader(new FileStream(caminho, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete));
            }

            while (leitor.ReadLine() is { } texto)
            {
                linhas.Add(LinhaLog.Interpretar(conteiner, texto));
            }

            return linhas;
        }

        public void Dispose() => leitor?.Dispose();
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
