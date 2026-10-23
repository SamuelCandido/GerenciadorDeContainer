using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class ServicoConteinerTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly IServicoConteiner servico;

    public ServicoConteinerTestes()
    {
        servico = new ServicoConteiner(diretorioTemporario);
    }

    [FatoWindows]
    public void ExecutarCriaContainerERodaComMetricas()
    {
        var conteiner = servico.Executar("c1", "scratch", "echo hello");

        Assert.Equal("c1", conteiner.Nome);
        Assert.Equal(EstadoConteiner.Finalizado, conteiner.Estado);
        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.NotNull(conteiner.IdProcesso);
        Assert.NotNull(conteiner.MemoriaUsadaBytes);
        Assert.NotNull(conteiner.TempoCpuMs);
        Assert.NotNull(conteiner.TotalProcessos);
    }

    [FatoWindows]
    public void ExecutarContabilizaOGrupoDeProcessosInteiro()
    {
        // O ping dura cerca de um segundo e roda como filho do cmd.exe. Isso
        // garante que a atribuicao ao Job Object vence a corrida e que ha dois
        // processos no grupo, provando que a contabilidade cobre os netos.
        var conteiner = servico.Executar("c1", "scratch", "ping -n 2 127.0.0.1 > nul");

        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.True(conteiner.TotalProcessos >= 2,
            $"Esperado ao menos 2 processos no grupo, obtido {conteiner.TotalProcessos}.");
        Assert.True(conteiner.MemoriaUsadaBytes > 0);
        Assert.True(conteiner.TempoCpuMs >= 0);
        Assert.NotNull(conteiner.FalhasDePagina);
    }

    [FatoWindows]
    public void ContainerNaoHerdaVariaveisDeAmbienteDoHost()
    {
        Environment.SetEnvironmentVariable("SEGREDO_DO_HOST", "senha-do-host-123");
        try
        {
            var saida = new List<string>();
            servico.Executar("c1", "scratch", "echo [%SEGREDO_DO_HOST%]", saida: saida.Add);

            // O cmd.exe deixa a referência literal quando a variável não existe.
            Assert.Contains("[%SEGREDO_DO_HOST%]", saida);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEGREDO_DO_HOST", null);
        }
    }

    [FatoWindows]
    public void ContainerRecebeAmbienteMinimoEVariaveisDoRun()
    {
        var opcoes = new OpcoesConteiner
        {
            Ambiente = new Dictionary<string, string> { ["SAUDACAO"] = "ola" }
        };
        var saida = new List<string>();

        var conteiner = servico.Executar("c1", "scratch", "echo %SystemRoot% %SAUDACAO%", opcoes, saida.Add);

        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.DoesNotContain("%SystemRoot%", saida.Single());
        Assert.EndsWith(" ola", saida.Single());
    }

    [FatoWindows]
    public void SaidaVaiParaOLogEAcumulaEntreExecucoes()
    {
        servico.Executar("c1", "scratch", "echo linha");
        servico.Iniciar("c1");

        var log = new List<string>();
        servico.LerLogs("c1", seguir: false, log.Add);

        Assert.Equal(["linha", "linha"], log);
    }

    [FatoWindows]
    public void PararEncerraOGrupoEMarcaParado()
    {
        servico.Criar("c1", "scratch", "ping -n 60 127.0.0.1");
        var dono = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");

        var parado = servico.Parar("c1");

        Assert.Equal(EstadoConteiner.Parado, parado.Estado);
        Assert.Equal(ServicoConteiner.CodigoSaidaParado, parado.CodigoSaida);
        Assert.True(dono.Wait(TimeSpan.FromSeconds(10)));
    }

    [FatoWindows]
    public void ContainerParadoPodeSerIniciadoDeNovo()
    {
        // Cada start acrescenta uma linha a um arquivo do rootfs. Depois de
        // stop + start, o arquivo tem duas linhas: é o mesmo container, com o
        // mesmo sistema de arquivos — não um novo criado da imagem.
        var original = servico.Criar("c1", "scratch", "echo iniciou>> vezes.txt & ping -n 60 127.0.0.1");
        var primeiro = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");
        servico.Parar("c1");
        primeiro.Wait(TimeSpan.FromSeconds(10));

        var segundo = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");

        var conteiner = servico.Obter("c1");
        Assert.Equal(EstadoConteiner.Executando, conteiner.Estado);
        Assert.Equal(original.Id, conteiner.Id);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(conteiner.CaminhoRootFs, "vezes.txt")).Length);

        servico.Parar("c1");
        segundo.Wait(TimeSpan.FromSeconds(10));
    }

    [FatoWindows]
    public void IniciarDuasVezesAoMesmoTempoFalha()
    {
        servico.Criar("c1", "scratch", "ping -n 60 127.0.0.1");
        var dono = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");

        Assert.Throws<InvalidOperationException>(() => servico.Iniciar("c1"));

        servico.Parar("c1");
        dono.Wait(TimeSpan.FromSeconds(10));
    }

    [FatoWindows]
    public void ExecRodaDentroDoGrupoDoContainer()
    {
        servico.Criar("c1", "scratch", "ping -n 60 127.0.0.1");
        var dono = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");

        var codigo = servico.ExecutarNoConteiner("c1", "echo oi > exec.txt");

        Assert.Equal(0, codigo);
        Assert.True(File.Exists(Path.Combine(servico.Obter("c1").CaminhoRootFs, "exec.txt")));
        Assert.True(servico.Estatisticas("c1").TotalProcessos >= 3);

        servico.Parar("c1");
        dono.Wait(TimeSpan.FromSeconds(10));
    }

    [FatoWindows]
    public void RemoverForcadoParaERemove()
    {
        servico.Criar("c1", "scratch", "ping -n 60 127.0.0.1");
        var dono = Task.Run(() => servico.Iniciar("c1"));
        EsperarExecucao("c1");

        Assert.Throws<InvalidOperationException>(() => servico.Remover("c1"));
        servico.Remover("c1", forcar: true);

        Assert.Empty(servico.Listar());
        Assert.True(dono.Wait(TimeSpan.FromSeconds(10)));
    }

    [FatoWindows]
    public void RemoverAoFinalizarApagaOContainer()
    {
        var opcoes = new OpcoesConteiner { RemoverAoFinalizar = true };

        servico.Executar("c1", "scratch", "echo tchau", opcoes);

        Assert.Empty(servico.Listar());
    }

    [FatoWindows]
    public void ContainerUsaOCmdEOsArquivosDaImagem()
    {
        var contexto = CriarContexto(
            "FROM scratch\nCOPY ola.txt .\nRUN copy ola.txt copia.txt\nCMD type copia.txt",
            ("ola.txt", "conteudo da imagem"));
        new ServicoImagem(diretorioTemporario).Construir(contexto, "app");
        var saida = new List<string>();

        var conteiner = servico.Executar("c1", "app", saida: saida.Add);

        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.Contains("conteudo da imagem", saida);
    }

    [Fact]
    public void CriarCopiaORootFsEOAmbienteDaImagem()
    {
        var contexto = CriarContexto(
            "FROM scratch\nCOPY dados/ /app/\nENV MODO=producao\nCMD type app\\a.txt",
            ("dados/a.txt", "a"));
        new ServicoImagem(diretorioTemporario).Construir(contexto, "app:1.0");
        var opcoes = new OpcoesConteiner
        {
            Ambiente = new Dictionary<string, string> { ["EXTRA"] = "1" }
        };

        var conteiner = servico.Criar("c1", "app:1.0", opcoes: opcoes);

        Assert.Equal(EstadoConteiner.Criado, conteiner.Estado);
        Assert.Equal("app:1.0", conteiner.Imagem);
        Assert.Equal(@"type app\a.txt", conteiner.Comando);
        Assert.Equal("producao", conteiner.Ambiente["MODO"]);
        Assert.Equal("1", conteiner.Ambiente["EXTRA"]);
        Assert.True(File.Exists(Path.Combine(conteiner.CaminhoRootFs, "app", "a.txt")));
    }

    [Fact]
    public void CriarSemComandoNemCmdFalha()
    {
        Assert.Throws<ArgumentException>(() => servico.Criar("c1", "scratch"));
        Assert.Empty(servico.Listar());
    }

    [Fact]
    public void CriarSemNomeGeraUm()
    {
        var conteiner = servico.Criar(null, "scratch", "echo oi");

        Assert.Matches("^[a-z]+_[a-z]+$", conteiner.Nome);
    }

    [Fact]
    public void CriarLancaErroComNomeDuplicado()
    {
        servico.Criar("c1", "scratch", "echo hello");

        Assert.Throws<InvalidOperationException>(() => servico.Criar("c1", "scratch", "echo again"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\..\Windows")]
    [InlineData("a/b")]
    [InlineData("com espaco")]
    public void CriarRejeitaNomesInvalidos(string nome)
    {
        Assert.Throws<ArgumentException>(() => servico.Criar(nome, "scratch", "echo hello"));
    }

    [Fact]
    public void CriarComImagemInexistenteFalha()
    {
        Assert.Throws<InvalidOperationException>(() => servico.Criar("c1", "nao-existe", "echo hello"));
    }

    [Fact]
    public void ObterAceitaPrefixoDoId()
    {
        var conteiner = servico.Criar("c1", "scratch", "echo hello");

        Assert.Equal("c1", servico.Obter(conteiner.Id[..4]).Nome);
    }

    [Fact]
    public void ListarRetornaTodosOsContainers()
    {
        servico.Criar("c1", "scratch", "echo one");
        servico.Criar("c2", "scratch", "echo two");

        Assert.Equal(2, servico.Listar().Count);
    }

    [Fact]
    public void RemoverApagaOContainerEORootFs()
    {
        var conteiner = servico.Criar("c1", "scratch", "echo hello");

        servico.Remover("c1");

        Assert.Empty(servico.Listar());
        Assert.False(Directory.Exists(conteiner.CaminhoRootFs));
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(-1L, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 101)]
    public void CriarRejeitaLimitesInvalidos(long? memoria, int? processos, int? cpu)
    {
        var opcoes = new OpcoesConteiner
        {
            Limites = new LimitesRecursos
            {
                MemoriaMaximaBytes = memoria,
                ProcessosMaximos = processos,
                PercentualMaximoCpu = cpu
            }
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => servico.Criar("c1", "scratch", "echo hello", opcoes));
        Assert.Empty(servico.Listar());
    }

    [Fact]
    public void PararLancaErroQuandoContainerNaoExiste()
    {
        Assert.Throws<InvalidOperationException>(() => servico.Parar("inexistente"));
    }

    [Fact]
    public void PararLancaErroQuandoContainerNaoEstaRodando()
    {
        servico.Criar("c1", "scratch", "echo hello");

        Assert.Throws<InvalidOperationException>(() => servico.Parar("c1"));
    }

    private void EsperarExecucao(string nome)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (servico.Obter(nome) is not { Estado: EstadoConteiner.Executando, IdProcesso: not null })
        {
            Assert.True(DateTime.UtcNow < limite, "O container nao entrou em execucao.");
            Thread.Sleep(20);
        }
    }

    private List<string> LerLog(string nome)
    {
        var linhas = new List<string>();
        servico.LerLogs(nome, seguir: false, linhas.Add);
        return linhas;
    }

    private string CriarContexto(string miniDockerfile, params (string Caminho, string Conteudo)[] arquivos)
    {
        var contexto = Path.Combine(diretorioTemporario, "contexto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contexto);
        File.WriteAllText(Path.Combine(contexto, ArquivoBuild.NomePadrao), miniDockerfile);

        foreach (var (caminho, conteudo) in arquivos)
        {
            var completo = Path.Combine(contexto, caminho);
            Directory.CreateDirectory(Path.GetDirectoryName(completo)!);
            File.WriteAllText(completo, conteudo);
        }

        return contexto;
    }

    public void Dispose()
    {
        if (Directory.Exists(diretorioTemporario))
        {
            Directory.Delete(diretorioTemporario, recursive: true);
        }
    }
}
