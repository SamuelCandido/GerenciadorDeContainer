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
        var conteiner = servico.Executar("c1", "echo hello");

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
        var conteiner = servico.Executar("c1", "ping -n 2 127.0.0.1 > nul");

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
            var conteiner = servico.Executar("c1", "echo [%SEGREDO_DO_HOST%] > saida.txt");
            var saida = File.ReadAllText(Path.Combine(conteiner.CaminhoRootFs, "saida.txt"));

            // O cmd.exe deixa a referência literal quando a variável não existe.
            Assert.Contains("%SEGREDO_DO_HOST%", saida);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEGREDO_DO_HOST", null);
        }
    }

    [FatoWindows]
    public void ContainerRecebeAmbienteMinimoFuncional()
    {
        var conteiner = servico.Executar("c1", "echo %SystemRoot% > saida.txt");
        var saida = File.ReadAllText(Path.Combine(conteiner.CaminhoRootFs, "saida.txt"));

        Assert.DoesNotContain("%SystemRoot%", saida);
        Assert.Equal(0, conteiner.CodigoSaida);
    }

    [FatoWindows]
    public void ExecutarLancaErroComNomeDuplicado()
    {
        servico.Executar("c1", "echo hello");

        Assert.Throws<InvalidOperationException>(() => servico.Executar("c1", "echo again"));
    }

    [FatoWindows]
    public void ListarRetornaTodosOsContainers()
    {
        servico.Executar("c1", "echo one");
        servico.Executar("c2", "echo two");

        var conteineres = servico.Listar();

        Assert.Equal(2, conteineres.Count);
    }

    [FatoWindows]
    public void RemoverApagaOContainer()
    {
        servico.Executar("c1", "echo hello");

        servico.Remover("c1");

        Assert.Empty(servico.Listar());
    }

    [Fact]
    public void ExecutarLancaErroComNomeVazio()
    {
        Assert.Throws<ArgumentException>(() => servico.Executar("", "echo hello"));
    }

    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(-1L, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 101)]
    public void ExecutarRejeitaLimitesInvalidos(long? memoria, int? processos, int? cpu)
    {
        var limites = new LimitesRecursos
        {
            MemoriaMaximaBytes = memoria,
            ProcessosMaximos = processos,
            PercentualMaximoCpu = cpu
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => servico.Executar("c1", "echo hello", limites));
    }

    [Fact]
    public void LimiteInvalidoNaoDeixaContainerPelaMetade()
    {
        var limites = new LimitesRecursos { PercentualMaximoCpu = 500 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => servico.Executar("c1", "echo hello", limites));
        Assert.Empty(servico.Listar());
    }

    [Fact]
    public void PararLancaErroQuandoContainerNaoExiste()
    {
        Assert.Throws<InvalidOperationException>(() => servico.Parar("inexistente"));
    }

    public void Dispose()
    {
        if (Directory.Exists(diretorioTemporario))
        {
            Directory.Delete(diretorioTemporario, recursive: true);
        }
    }
}
