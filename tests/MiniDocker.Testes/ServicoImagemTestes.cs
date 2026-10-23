using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class ServicoImagemTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly ServicoImagem servico;

    public ServicoImagemTestes()
    {
        servico = new ServicoImagem(diretorioTemporario);
    }

    [Fact]
    public void ConstruirCopiaArquivosEGuardaEnvECmd()
    {
        var contexto = CriarContexto(
            "# comentario\nFROM scratch\nCOPY a.txt dados/\nENV CHAVE=valor\nCMD type dados\\a.txt",
            ("a.txt", "conteudo"));

        var imagem = servico.Construir(contexto, "app");

        Assert.Equal("app:latest", imagem.Referencia);
        Assert.Equal("valor", imagem.Ambiente["CHAVE"]);
        Assert.Equal(@"type dados\a.txt", imagem.Comando);
        Assert.Equal("conteudo", File.ReadAllText(Path.Combine(imagem.CaminhoRootFs, "dados", "a.txt")));
        Assert.Equal(8, imagem.TamanhoBytes);
        Assert.Equal(4, imagem.Historico.Count);
    }

    [Fact]
    public void FromHerdaArquivosEConfiguracaoDaBase()
    {
        servico.Construir(CriarContexto("FROM scratch\nCOPY a.txt .\nENV A=1\nCMD echo base", ("a.txt", "a")), "base");

        var derivada = servico.Construir(
            CriarContexto("FROM base\nCOPY b.txt .\nENV B=2", ("b.txt", "b")), "derivada:v2");

        Assert.Equal("echo base", derivada.Comando);
        Assert.Equal("1", derivada.Ambiente["A"]);
        Assert.Equal("2", derivada.Ambiente["B"]);
        Assert.True(File.Exists(Path.Combine(derivada.CaminhoRootFs, "a.txt")));
        Assert.True(File.Exists(Path.Combine(derivada.CaminhoRootFs, "b.txt")));
    }

    [Fact]
    public void ConstruirDeNovoSubstituiAImagemComAMesmaReferencia()
    {
        var primeira = servico.Construir(CriarContexto("FROM scratch\nCMD echo 1"), "app");
        var segunda = servico.Construir(CriarContexto("FROM scratch\nCMD echo 2"), "app");

        Assert.Equal(segunda.Id, servico.Listar().Single().Id);
        Assert.False(Directory.Exists(primeira.CaminhoRootFs));
    }

    [Fact]
    public void CopyNaoSaiDoContextoDeBuild()
    {
        var contexto = CriarContexto("FROM scratch\nCOPY ../fora.txt .");

        Assert.Throws<InvalidOperationException>(() => servico.Construir(contexto, "app"));
        Assert.Empty(servico.Listar());
    }

    [Theory]
    [InlineData("COPY a b")]
    [InlineData("FROM scratch\nFROM scratch")]
    [InlineData("FROM scratch\nEXPOSE 80")]
    [InlineData("FROM")]
    public void MiniDockerfileInvalidoELancaErro(string conteudo)
    {
        Assert.Throws<FormatException>(() => ArquivoBuild.Interpretar(conteudo.Split('\n')));
    }

    [Theory]
    [InlineData("App")]
    [InlineData("app:")]
    [InlineData("../app")]
    public void ReferenciaInvalidaELancaErro(string referencia)
    {
        Assert.Throws<ArgumentException>(() => servico.Construir(CriarContexto("FROM scratch"), referencia));
    }

    [Fact]
    public void ObterAceitaScratchSemRegistro()
    {
        Assert.True(servico.Obter("scratch").EhScratch);
        Assert.Throws<InvalidOperationException>(() => servico.Remover("scratch"));
    }

    [Fact]
    public void SalvarECarregarPreservamAImagem()
    {
        var original = servico.Construir(
            CriarContexto("FROM scratch\nCOPY pasta/ pasta/\nENV X=1\nCMD echo oi", ("pasta/sub/a.txt", "a")),
            "app:1.0");
        var arquivo = Path.Combine(diretorioTemporario, "app.zip");
        servico.Exportar("app:1.0", arquivo);
        servico.Remover("app:1.0");

        var carregada = servico.Importar(arquivo);

        Assert.Equal(original.Id, carregada.Id);
        Assert.Equal("app:1.0", carregada.Referencia);
        Assert.Equal("echo oi", carregada.Comando);
        Assert.Equal("1", carregada.Ambiente["X"]);
        Assert.Equal("a", File.ReadAllText(Path.Combine(carregada.CaminhoRootFs, "pasta", "sub", "a.txt")));
    }

    [Fact]
    public void CommitCongelaORootFsDoContainer()
    {
        var conteineres = new ServicoConteiner(diretorioTemporario);
        var conteiner = conteineres.Criar("c1", "scratch", "echo oi");
        File.WriteAllText(Path.Combine(conteiner.CaminhoRootFs, "estado.txt"), "alterado");

        var imagem = servico.Commit(conteiner, "snapshot");

        Assert.Equal("echo oi", imagem.Comando);
        Assert.Equal("alterado", File.ReadAllText(Path.Combine(imagem.CaminhoRootFs, "estado.txt")));

        // A imagem é uma cópia: mexer no container depois não a afeta.
        File.WriteAllText(Path.Combine(conteiner.CaminhoRootFs, "estado.txt"), "de novo");
        Assert.Equal("alterado", File.ReadAllText(Path.Combine(imagem.CaminhoRootFs, "estado.txt")));
    }

    [Fact]
    public void ObterAceitaPrefixoDoId()
    {
        var imagem = servico.Construir(CriarContexto("FROM scratch"), "app");

        Assert.Equal(imagem.Id, servico.Obter(imagem.Id[..5]).Id);
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
