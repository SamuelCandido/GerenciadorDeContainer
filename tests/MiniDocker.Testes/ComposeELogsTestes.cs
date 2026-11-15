using MiniDocker.Nucleo.Compose;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class ComposeELogsTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly ServicoConteiner servico;
    private readonly ServicoImagem imagens;

    public ComposeELogsTestes()
    {
        servico = new ServicoConteiner(diretorioTemporario);
        imagens = new ServicoImagem(diretorioTemporario);
    }

    [Fact]
    public void LeServicosOpcoesEOrdemDeDependencia()
    {
        var compose = Interpretar("""
            # comentario
            projeto = relatorios

            [painel]
            build   = painel
            porta   = 8080:80
            volume  = ./fila:fila:ro
            env     = TITULO=Logs
            env     = MODO=producao
            restart = always
            depende = processador

            [processador]
            imagem   = processador:2.0
            replicas = 3
            volume   = fila:fila
            memoria  = 64m
            pids     = 10
            restart  = on-failure:3
            """);

        Assert.Equal("relatorios", compose.Projeto);
        Assert.Equal(["processador", "painel"], compose.Servicos.Select(s => s.Nome));

        var processador = compose.Servicos[0];
        Assert.Equal("processador:2.0", processador.Imagem);
        Assert.Null(processador.Build);
        Assert.Equal(3, processador.Replicas);
        Assert.Equal(["fila:fila"], processador.Volumes);
        Assert.Equal(64L << 20, processador.Limites.MemoriaMaximaBytes);
        Assert.Equal(10, processador.Limites.ProcessosMaximos);
        Assert.Equal(3, processador.Reinicio.MaximoTentativas);

        var painel = compose.Servicos[1];
        Assert.Equal("painel", painel.Imagem);
        Assert.Equal(Path.Combine(diretorioTemporario, "painel"), painel.Build);
        Assert.Equal(["8080:80"], painel.Portas);
        Assert.Equal([$"{Path.Combine(diretorioTemporario, "fila")}:fila:ro"], painel.Volumes);
        Assert.Equal("Logs", painel.Ambiente["TITULO"]);
        Assert.Equal("producao", painel.Ambiente["MODO"]);
        Assert.Equal(ModoReinicio.Sempre, painel.Reinicio.Modo);
    }

    [Fact]
    public void SemProjetoUsaONomeDaPasta()
    {
        var compose = ArquivoCompose.Interpretar(["[web]", "imagem = x"], Path.Combine(diretorioTemporario, "Meu Sistema"));

        Assert.Equal("meu-sistema", compose.Projeto);
    }

    [Theory]
    [InlineData("[a]\nimagem = x\ndepende = b\n[b]\nimagem = y\ndepende = a", "circular")]
    [InlineData("[a]\nimagem = x\ndepende = nada", "nao existe")]
    [InlineData("[a]\nbuild = .\nreplicas = 2\nporta = 8080:80", "porta fixa")]
    [InlineData("[a]\nimagem = x\ncor = azul", "Linha 3")]
    [InlineData("[a]\nreplicas = 2", "imagem' ou 'build")]
    [InlineData("imagem = x", "fora de um servico")]
    [InlineData("[A]\nimagem = x", "invalido")]
    [InlineData("[a]\nimagem = x\n[a]\nimagem = y", "duas vezes")]
    [InlineData("# vazio", "nenhum servico")]
    [InlineData("[a]\nimagem = x\nreplicas = zero", "replicas")]
    public void ErrosDizemALinhaEOMotivo(string conteudo, string trecho)
    {
        var erro = Assert.Throws<FormatException>(() => Interpretar(conteudo));

        Assert.Contains(trecho, erro.Message);
    }

    [Fact]
    public void SubirCriaAsReplicasEDescerRemoveSoOProjeto()
    {
        CriarPasta("app", ("app.bat", "@echo off"));
        File.WriteAllText(Path.Combine(diretorioTemporario, ArquivoCompose.NomePadrao), """
            projeto = loja
            [worker]
            build = app
            replicas = 2
            volume = dados:dados
            [web]
            build = app
            porta = 8080:80
            depende = worker
            """);
        servico.Criar("de-fora", "scratch", "echo oi");
        var compose = new ServicoCompose(servico, imagens);
        var definicao = ArquivoCompose.Ler(Path.Combine(diretorioTemporario, ArquivoCompose.NomePadrao));

        var conteineres = compose.Subir(definicao);

        Assert.Equal(["worker-1", "worker-2", "web-1"], conteineres.Select(c => c.Nome));
        Assert.All(conteineres, c => Assert.Equal("loja", c.Projeto));
        Assert.Equal("app.bat", servico.Obter("web-1").Comando);
        Assert.Equal(8080, servico.Obter("web-1").Portas.Single().PortaHost);

        // Subir de novo não duplica nada.
        Assert.Equal(3, compose.Subir(definicao).Count);

        Assert.Equal(3, compose.Derrubar("loja").Count);
        Assert.Equal(["de-fora"], servico.Listar().Select(c => c.Nome));
    }

    [Fact]
    public void SubirComOutrasReplicasEscala()
    {
        CriarPasta("app", ("app.bat", ""));
        var primeira = Interpretar("[worker]\nbuild = app\nreplicas = 3");
        var compose = new ServicoCompose(servico, imagens);
        compose.Subir(primeira);

        compose.Subir(Interpretar("[worker]\nbuild = app\nreplicas = 1"));

        Assert.Equal(["worker-1"], servico.ListarGrupo("worker").Select(c => c.Nome));
    }

    [Fact]
    public void ServicoComNomeDeContainerDeOutroProjetoFalha()
    {
        servico.Criar("web-1", "scratch", "x", new OpcoesConteiner { Grupo = "web" });

        Assert.Throws<InvalidOperationException>(() =>
            new ServicoCompose(servico, imagens).Subir(Interpretar("projeto = loja\n[web]\nimagem = scratch\ncomando = x")));
    }

    [Theory]
    [InlineData("2026-11-15T10:00:00.1234567Z ola mundo", "ola mundo", true)]
    [InlineData("2026-11-15T10:00:00.1234567Z ", "", true)]
    [InlineData("linha antiga, sem horario", "linha antiga, sem horario", false)]
    [InlineData("12:30 nao e um horario completo", "12:30 nao e um horario completo", false)]
    public void LinhaDeLogSeparaOHorario(string gravada, string texto, bool temHorario)
    {
        var linha = LinhaLog.Interpretar("c1", gravada);

        Assert.Equal(texto, linha.Texto);
        Assert.Equal(temHorario, linha.Momento is not null);
    }

    [Fact]
    public void LogsDeVariasReplicasSaemIntercaladosPeloHorario()
    {
        var a = servico.Criar("w-1", "scratch", "x");
        var b = servico.Criar("w-2", "scratch", "x");
        var inicio = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        GravarLog(a, (inicio, "a1"), (inicio.AddSeconds(2), "a2"));
        GravarLog(b, (inicio.AddSeconds(1), "b1"), (inicio.AddSeconds(3), "b2"));

        var lidas = new List<LinhaLog>();
        servico.LerLogs(["w-1", "w-2"], seguir: false, lidas.Add);

        Assert.Equal(["a1", "b1", "a2", "b2"], lidas.Select(l => l.Texto));
        Assert.Equal(["w-1", "w-2", "w-1", "w-2"], lidas.Select(l => l.Conteiner));
    }

    [Fact]
    public void VolumeSoLeituraEGravavelNaMesmaPastaConflitam()
    {
        var pasta = Path.Combine(diretorioTemporario, "fila");
        var gravavel = servico.Criar("proc", "scratch", "x", new OpcoesConteiner { Volumes = [$"{pasta}:fila"] });
        var leitor = servico.Criar("painel", "scratch", "x", new OpcoesConteiner { Volumes = [$"{pasta}/saida:saida:ro"] });
        var igual = servico.Criar("outro", "scratch", "x", new OpcoesConteiner { Volumes = [$"{pasta}:fila"] });

        Assert.Throws<InvalidOperationException>(() => ServicoConteiner.VerificarConflitoDeVolumes(leitor, [gravavel]));
        ServicoConteiner.VerificarConflitoDeVolumes(igual, [gravavel]);
    }

    private void GravarLog(Conteiner conteiner, params (DateTime Momento, string Texto)[] linhas)
    {
        File.WriteAllLines(Path.Combine(diretorioTemporario, "logs", $"{conteiner.Id}.log"),
            linhas.Select(l => LinhaLog.Formatar(l.Momento, l.Texto)));
    }

    private DefinicaoCompose Interpretar(string conteudo) =>
        ArquivoCompose.Interpretar(conteudo.Split('\n'), diretorioTemporario);

    private void CriarPasta(string nome, params (string Arquivo, string Conteudo)[] arquivos)
    {
        var pasta = Path.Combine(diretorioTemporario, nome);
        Directory.CreateDirectory(pasta);
        foreach (var (arquivo, conteudo) in arquivos)
        {
            File.WriteAllText(Path.Combine(pasta, arquivo), conteudo);
        }
    }

    public void Dispose()
    {
        foreach (var conteiner in servico.Listar())
        {
            servico.Remover(conteiner.Nome, forcar: true);
        }

        if (Directory.Exists(diretorioTemporario))
        {
            Directory.Delete(diretorioTemporario, recursive: true);
        }
    }
}
