using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class VolumeTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly ServicoConteiner servico;
    private readonly ServicoVolume volumes;

    public VolumeTestes()
    {
        servico = new ServicoConteiner(diretorioTemporario);
        volumes = new ServicoVolume(diretorioTemporario);
    }

    [Theory]
    [InlineData(@"C:\dados:dados", @"C:\dados", "dados", false)]
    [InlineData("backups:/destino", "backups", "/destino", false)]
    [InlineData(@".\pasta:app\dados", @".\pasta", @"app\dados", false)]
    [InlineData("/tmp/x:dados", "/tmp/x", "dados", false)]
    [InlineData(@"C:\docs:origem:ro", @"C:\docs", "origem", true)]
    [InlineData(@"C:\docs:origem:rw", @"C:\docs", "origem", false)]
    public void SepararEntendeOrigemDestinoEModo(string especificacao, string origem, string destino, bool ro)
    {
        Assert.Equal((origem, destino, ro), EspecificacaoVolume.Separar(especificacao));
    }

    [Fact]
    public void SomenteLeituraFicaRegistradoEExigeConfinamento()
    {
        var host = Path.Combine(diretorioTemporario, "host");

        var conteiner = servico.Criar("c1", "scratch", "echo oi", ComVolumes($"{host}:origem:ro"));
        Assert.True(conteiner.Volumes.Single().SomenteLeitura);

        var privilegiado = new OpcoesConteiner { Volumes = [$"{host}:origem:ro"], Privilegiado = true };
        Assert.Throws<ArgumentException>(() => servico.Criar("c2", "scratch", "echo oi", privilegiado));
    }

    [FatoWindows]
    public void VolumeSomenteLeituraNaoAceitaEscrita()
    {
        var host = Path.Combine(diretorioTemporario, "host");
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, "original.txt"), "do host");
        var saida = new List<string>();

        var conteiner = servico.Executar("c1", "scratch",
            @"type origem\original.txt & echo alterado> origem\original.txt",
            ComVolumes($@"{host}:origem:ro"), saida.Add);

        Assert.Contains("do host", saida);                 // leu
        Assert.NotEqual(0, conteiner.CodigoSaida);         // não escreveu
        Assert.Equal("do host", File.ReadAllText(Path.Combine(host, "original.txt")));
    }

    [Theory]
    [InlineData("semdestino")]
    [InlineData("canil:")]
    [InlineData(":dados")]
    [InlineData(@"C:\dados:D:\outro")]
    [InlineData("backups:destino:xx")]
    public void SepararRecusaEspecificacaoInvalida(string especificacao)
    {
        Assert.Throws<ArgumentException>(() => EspecificacaoVolume.Separar(especificacao));
    }

    [Theory]
    [InlineData("canil", true)]
    [InlineData("dados-1", true)]
    [InlineData(@"C:\dados", false)]
    [InlineData(@".\dados", false)]
    [InlineData("/tmp/dados", false)]
    public void EhNomeadoSegueARegraDoDocker(string origem, bool nomeado)
    {
        Assert.Equal(nomeado, EspecificacaoVolume.EhNomeado(origem));
    }

    [Fact]
    public void VolumeNomeadoSobreviveAoRmDoContainer()
    {
        var conteiner = servico.Criar("c1", "scratch", "echo oi", ComVolumes("canil:dados"));
        File.WriteAllText(Path.Combine(conteiner.CaminhoRootFs, "dados", "vezes.txt"), "3");

        servico.Remover("c1");

        // O rootfs sumiu, mas o arquivo gravado pelo volume continua lá.
        Assert.False(Directory.Exists(conteiner.CaminhoRootFs));
        var volume = volumes.Obter("canil");
        Assert.Equal("3", File.ReadAllText(Path.Combine(volume.Caminho, "vezes.txt")));

        // Um container novo com o mesmo volume enxerga o arquivo.
        var outro = servico.Criar("c2", "scratch", "echo oi", ComVolumes("canil:dados"));
        Assert.Equal("3", File.ReadAllText(Path.Combine(outro.CaminhoRootFs, "dados", "vezes.txt")));
    }

    [Fact]
    public void PastaDoHostMontadaNaoEApagadaPeloRm()
    {
        var host = Path.Combine(diretorioTemporario, "host");
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, "importante.txt"), "nao apague");

        var conteiner = servico.Criar("c1", "scratch", "echo oi", ComVolumes($"{host}:app/dados"));
        Assert.True(File.Exists(Path.Combine(conteiner.CaminhoRootFs, "app", "dados", "importante.txt")));

        servico.Remover("c1");

        Assert.Equal("nao apague", File.ReadAllText(Path.Combine(host, "importante.txt")));
        Assert.Equal(host, conteiner.Volumes.Single().Origem);
    }

    [Fact]
    public void VolumeEmUsoNaoPodeSerRemovido()
    {
        servico.Criar("c1", "scratch", "echo oi", ComVolumes("canil:dados"));

        var erro = Assert.Throws<InvalidOperationException>(() => volumes.Remover("canil"));
        Assert.Contains("c1", erro.Message);
        Assert.Equal(["c1"], volumes.Listar().Single().Conteineres);

        servico.Remover("c1");
        volumes.Remover("canil");
        Assert.Empty(volumes.Listar());
    }

    [Fact]
    public void VolumeNomeadoVazioRecebeOConteudoDaImagem()
    {
        var contexto = Path.Combine(diretorioTemporario, "contexto");
        Directory.CreateDirectory(Path.Combine(contexto, "dados"));
        File.WriteAllText(Path.Combine(contexto, "dados", "inicial.txt"), "da imagem");
        new ServicoImagem(diretorioTemporario).Construir(contexto, "app", comando: "echo oi");

        servico.Criar("c1", "app", opcoes: ComVolumes("canil:dados"));

        Assert.Equal("da imagem", File.ReadAllText(Path.Combine(volumes.Obter("canil").Caminho, "inicial.txt")));
    }

    [Fact]
    public void CommitNaoLevaOsDadosDoVolume()
    {
        var conteiner = servico.Criar("c1", "scratch", "echo oi", ComVolumes("canil:dados"));
        File.WriteAllText(Path.Combine(conteiner.CaminhoRootFs, "dados", "segredo.txt"), "x");
        File.WriteAllText(Path.Combine(conteiner.CaminhoRootFs, "proprio.txt"), "y");

        var imagem = new ServicoImagem(diretorioTemporario).Commit(conteiner, "snapshot");

        Assert.True(File.Exists(Path.Combine(imagem.CaminhoRootFs, "proprio.txt")));
        Assert.False(Directory.Exists(Path.Combine(imagem.CaminhoRootFs, "dados")));
    }

    [Theory]
    [InlineData("canil:.")]
    [InlineData("canil:../fora")]
    [InlineData("nome invalido:dados")]
    [InlineData("canil:dados")] // duplicado com o segundo volume abaixo
    public void VolumeInvalidoNaoDeixaContainerPelaMetade(string especificacao)
    {
        Assert.ThrowsAny<Exception>(() =>
            servico.Criar("c1", "scratch", "echo oi", ComVolumes(especificacao, "outro:dados")));

        Assert.Empty(servico.Listar());
        Assert.False(Directory.Exists(Path.Combine(diretorioTemporario, "containers", "c1")));
    }

    [FatoWindows]
    public void ContainerEscreveNoVolumeMesmoConfinado()
    {
        var host = Path.Combine(diretorioTemporario, "host");
        Directory.CreateDirectory(host);

        var conteiner = servico.Executar("c1", "scratch", @"echo do container> dados\saida.txt",
            ComVolumes($@"{host}:dados"));

        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.Equal("do container", File.ReadAllText(Path.Combine(host, "saida.txt")).Trim());
    }

    private static OpcoesConteiner ComVolumes(params string[] especificacoes) =>
        new() { Volumes = especificacoes };

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
