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

    [Fact]
    public void ExecutarCriaContainerERodaComMetricas()
    {
        var conteiner = servico.Executar("c1", "echo hello");

        Assert.Equal("c1", conteiner.Nome);
        Assert.Equal(EstadoConteiner.Finalizado, conteiner.Estado);
        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.NotNull(conteiner.IdProcesso);
        Assert.NotNull(conteiner.MemoriaUsadaBytes);
        Assert.NotNull(conteiner.TempoCpuMs);
    }

    [Fact]
    public void ExecutarLancaErroComNomeDuplicado()
    {
        servico.Executar("c1", "echo hello");

        Assert.Throws<InvalidOperationException>(() => servico.Executar("c1", "echo again"));
    }

    [Fact]
    public void ExecutarLancaErroComNomeVazio()
    {
        Assert.Throws<ArgumentException>(() => servico.Executar("", "echo hello"));
    }

    [Fact]
    public void ListarRetornaTodosOsContainers()
    {
        servico.Executar("c1", "echo one");
        servico.Executar("c2", "echo two");

        var conteineres = servico.Listar();

        Assert.Equal(2, conteineres.Count);
    }

    [Fact]
    public void RemoverApagaOContainer()
    {
        servico.Executar("c1", "echo hello");

        servico.Remover("c1");

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