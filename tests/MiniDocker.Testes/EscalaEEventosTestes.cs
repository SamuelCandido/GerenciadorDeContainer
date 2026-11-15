using MiniDocker.Nucleo.Interop;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class EscalaEEventosTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly ServicoConteiner servico;
    private readonly RegistroEventos eventos;

    public EscalaEEventosTestes()
    {
        servico = new ServicoConteiner(diretorioTemporario);
        eventos = new RegistroEventos(diretorioTemporario);
    }

    [Theory]
    [InlineData("no", ModoReinicio.Nao, null)]
    [InlineData("always", ModoReinicio.Sempre, null)]
    [InlineData("on-failure", ModoReinicio.EmFalha, null)]
    [InlineData("on-failure:3", ModoReinicio.EmFalha, 3)]
    public void InterpretaARestartDoDocker(string texto, ModoReinicio modo, int? maximo)
    {
        var politica = PoliticaReinicio.Interpretar(texto);

        Assert.Equal(modo, politica.Modo);
        Assert.Equal(maximo, politica.MaximoTentativas);
        Assert.Equal(texto, politica.ToString());
    }

    [Theory]
    [InlineData("sempre")]
    [InlineData("on-failure:0")]
    [InlineData("on-failure:x")]
    [InlineData("always:2")]
    public void RecusaRestartInvalido(string texto)
    {
        Assert.Throws<ArgumentException>(() => PoliticaReinicio.Interpretar(texto));
    }

    [Fact]
    public void OnFailureReiniciaSoEmErroEAteOLimite()
    {
        var politica = PoliticaReinicio.Interpretar("on-failure:2");

        Assert.False(politica.DeveReiniciar(0, 0));
        Assert.True(politica.DeveReiniciar(1, 0));
        Assert.True(politica.DeveReiniciar(1, 1));
        Assert.False(politica.DeveReiniciar(1, 2));
        Assert.True(PoliticaReinicio.Interpretar("always").DeveReiniciar(0, 99));
    }

    [Fact]
    public void EsperaEntreReiniciosCresceAteUmTeto()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), PoliticaReinicio.Espera(0));
        Assert.Equal(TimeSpan.FromSeconds(2), PoliticaReinicio.Espera(1));
        Assert.Equal(TimeSpan.FromSeconds(4), PoliticaReinicio.Espera(2));
        Assert.Equal(TimeSpan.FromSeconds(30), PoliticaReinicio.Espera(20));
    }

    [Fact]
    public void RmERestartJuntosSaoRecusados()
    {
        var opcoes = new OpcoesConteiner { RemoverAoFinalizar = true, Reinicio = PoliticaReinicio.Interpretar("always") };

        Assert.Throws<ArgumentException>(() => servico.Criar("c1", "scratch", "echo oi", opcoes));
    }

    [Theory]
    [InlineData(0, null, false, null)]
    [InlineData(3, null, false, "terminou com erro (codigo 3)")]
    [InlineData(unchecked((int)0xC0000005), null, false, "falha: 0xC0000005 (violacao de acesso a memoria)")]
    [InlineData(1, "limite de memoria atingido pelo processo 42", false, "limite de memoria atingido pelo processo 42")]
    [InlineData(137, null, true, "parado pelo usuario (stop)")]
    [InlineData(null, null, false, "falha ao executar")]
    public void DescreveOMotivoDoTermino(int? codigo, string? erroKernel, bool parado, string? esperado)
    {
        Assert.Equal(esperado, ServicoConteiner.DescreverTermino(codigo, erroKernel, parado));
    }

    [Fact]
    public void EventoDoKernelDescreveOErro()
    {
        var evento = new EventoKernel(TipoEventoKernel.FimAnormal, 1234, unchecked((int)0xC00000FD));

        Assert.True(evento.EhErro);
        Assert.Equal("processo 1234 terminou com falha: 0xC00000FD (estouro de pilha)", evento.Descricao);
        Assert.False(new EventoKernel(TipoEventoKernel.NovoProcesso, 1, null).EhErro);
    }

    [Fact]
    public void EscalarCriaERemoveReplicasCopiandoAConfiguracao()
    {
        var opcoes = new OpcoesConteiner
        {
            Grupo = "worker",
            Ambiente = new Dictionary<string, string> { ["INTERVALO"] = "5" },
            Volumes = ["fila:fila"],
            Reinicio = PoliticaReinicio.Interpretar("on-failure:3"),
            Limites = new LimitesRecursos { ProcessosMaximos = 10 }
        };
        servico.Criar("worker-1", "scratch", "processador.bat", opcoes);

        var novas = servico.Escalar("worker", 3);

        Assert.Equal(["worker-2", "worker-3"], novas.Select(c => c.Nome));
        Assert.Equal(["worker-1", "worker-2", "worker-3"], servico.ListarGrupo("worker").Select(c => c.Nome));
        var copia = servico.Obter("worker-3");
        Assert.Equal("processador.bat", copia.Comando);
        Assert.Equal("5", copia.Ambiente["INTERVALO"]);
        Assert.Equal("fila", copia.Volumes.Single().NomeVolume);
        Assert.Equal(3, copia.Reinicio.MaximoTentativas);
        Assert.Equal(10, copia.Limites.ProcessosMaximos);

        Assert.Empty(servico.Escalar("worker", 1));
        Assert.Equal(["worker-1"], servico.ListarGrupo("worker").Select(c => c.Nome));

        // Ao crescer de novo, a numeração segue a partir da maior réplica existente.
        Assert.Equal(["worker-2"], servico.Escalar("worker", 2).Select(c => c.Nome));
    }

    [Fact]
    public void EscalarGrupoInexistenteFalha()
    {
        Assert.Throws<InvalidOperationException>(() => servico.Escalar("ninguem", 2));
    }

    [Fact]
    public void CriarERemoverFicamNoHistoricoDeEventos()
    {
        servico.Criar("c1", "scratch", "echo oi");
        servico.Remover("c1");

        var historico = eventos.LerTodos();

        Assert.Equal([TiposEvento.Criado, TiposEvento.Removido], historico.Select(e => e.Tipo));
        Assert.All(historico, e => Assert.Equal("c1", e.Conteiner));
    }

    [Fact]
    public void LeituraIncrementalSoDevolveEventosNovos()
    {
        eventos.Registrar("a", TiposEvento.Iniciado);
        var lidos = new List<Evento>();
        var posicao = eventos.Ler(0, lidos.Add);

        eventos.Registrar("a", TiposEvento.Erro, "limite de memoria");
        eventos.Ler(posicao, lidos.Add);

        Assert.Equal(2, lidos.Count);
        Assert.True(lidos[1].EhErro);
    }

    [Fact]
    public void VariosProcessosGravandoAoMesmoTempoNaoPerdemEventos()
    {
        Parallel.For(0, 200, i => new RegistroEventos(diretorioTemporario).Registrar($"c{i}", TiposEvento.Iniciado));

        Assert.Equal(200, eventos.LerTodos().Count);
    }

    [FatoWindows]
    public void OnFailureReiniciaNoMesmoContainerERegistraOMotivo()
    {
        var opcoes = new OpcoesConteiner { Reinicio = PoliticaReinicio.Interpretar("on-failure:2") };

        var conteiner = servico.Executar("c1", "scratch", "echo tentativa>> tentativas.txt & exit 3", opcoes);

        Assert.Equal(2, conteiner.Reinicios);
        Assert.Equal(3, conteiner.CodigoSaida);
        Assert.Equal("terminou com erro (codigo 3)", conteiner.MotivoTermino);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(conteiner.CaminhoRootFs, "tentativas.txt")).Length);
        Assert.Equal(2, eventos.LerTodos().Count(e => e.Tipo == TiposEvento.Reiniciando));
        Assert.Equal(TiposEvento.Falhou, eventos.LerTodos().Last().Tipo);
    }

    [FatoWindows]
    public void LimiteDeProcessosViraEventoDeErro()
    {
        var opcoes = new OpcoesConteiner { Limites = new LimitesRecursos { ProcessosMaximos = 1 } };

        // O cmd.exe é o único processo permitido: o ping é recusado pelo kernel.
        var conteiner = servico.Executar("c1", "scratch", "ping -n 1 127.0.0.1", opcoes);

        Assert.NotEqual(0, conteiner.CodigoSaida);
        Assert.Contains("limite de processos", conteiner.MotivoTermino);
        Assert.Contains(eventos.LerTodos(), e => e.Tipo == TiposEvento.Erro && e.Detalhe!.Contains("limite de processos"));
    }

    [FatoWindows]
    public void ContainerRecebeOProprioNomeEmHostname()
    {
        var saida = new List<string>();

        servico.Executar("worker-7", "scratch", "echo %HOSTNAME%", saida: saida.Add);

        Assert.Equal(["worker-7"], saida);
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
