using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

/// <summary>Um workspace com o alpine baixado, compartilhado pelos testes da classe.</summary>
public sealed class WorkspaceComAlpine : IDisposable
{
    public string Diretorio { get; } = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));

    public WorkspaceComAlpine()
    {
        if (OperatingSystem.IsLinux())
        {
            new ServicoImagem(Diretorio).Baixar("alpine");
        }
    }

    public void Dispose()
    {
        foreach (var conteiner in new ServicoConteiner(Diretorio).Listar())
        {
            new ServicoConteiner(Diretorio).Remover(conteiner.Nome, forcar: true);
        }

        if (Directory.Exists(Diretorio))
        {
            Directory.Delete(Diretorio, recursive: true);
        }
    }
}

public class RuntimeLinuxTestes(WorkspaceComAlpine workspace) : IClassFixture<WorkspaceComAlpine>
{
    private readonly ServicoConteiner servico = new(workspace.Diretorio);

    [FatoLinux]
    public void ImagemBaixadaELinuxComCmdEAmbiente()
    {
        var imagem = new ServicoImagem(workspace.Diretorio).Obter("alpine");

        Assert.Equal(Imagem.PlataformaLinux, imagem.Plataforma);
        Assert.Equal("/bin/sh", imagem.Comando);
        Assert.Contains("PATH", imagem.Ambiente.Keys);
        Assert.True(File.Exists(imagem.CaminhoRootFs));
    }

    [FatoLinux]
    public void ContainerSoEnxergaOsProprosProcessosEOProprioHostname()
    {
        var saida = new List<string>();

        var conteiner = servico.Executar(Nome(), "alpine", "hostname; ps -o pid | tail -n +2 | wc -l", saida: saida.Add);

        Assert.Equal(0, conteiner.CodigoSaida);
        Assert.Equal(conteiner.Nome, saida[0]);
        // init + sh + ps + tail + wc: poucos processos, nenhum do host.
        Assert.True(int.Parse(saida[1].Trim()) < 10, $"Processos visiveis: {saida[1]}");
    }

    [FatoLinux]
    public void ContainerRodaComoRootENaoVeOAmbienteDoHost()
    {
        Environment.SetEnvironmentVariable("SEGREDO_DO_HOST", "senha-123");
        try
        {
            var saida = new List<string>();
            var opcoes = new OpcoesConteiner { Ambiente = new Dictionary<string, string> { ["MODO"] = "teste" } };

            servico.Executar(Nome(), "alpine", "id -u; echo \"[$SEGREDO_DO_HOST] [$MODO]\"", opcoes, saida.Add);

            Assert.Equal(["0", "[] [teste]"], saida);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEGREDO_DO_HOST", null);
        }
    }

    [FatoLinux]
    public void StopEStartMantemOsArquivosDoContainer()
    {
        var nome = Nome();
        servico.Criar(nome, "alpine", "echo x >> /vezes; sleep 60");

        var primeiro = Task.Run(() => servico.Iniciar(nome));
        EsperarLinhas(nome, "vezes", 1);
        var parado = servico.Parar(nome);
        primeiro.Wait(TimeSpan.FromSeconds(10));

        Assert.Equal(EstadoConteiner.Parado, parado.Estado);
        Assert.Equal(ServicoConteiner.CodigoSaidaParado, parado.CodigoSaida);

        // Mesmo container, mesmo arquivo: a segunda execução acrescenta uma linha.
        var segundo = Task.Run(() => servico.Iniciar(nome));
        EsperarLinhas(nome, "vezes", 2);

        servico.Parar(nome);
        segundo.Wait(TimeSpan.FromSeconds(10));
    }

    [FatoLinux]
    public void ExecEntraNosNamespacesDoContainer()
    {
        var nome = Nome();
        servico.Criar(nome, "alpine", "echo dentro > /marca; sleep 60");
        var dono = Task.Run(() => servico.Iniciar(nome));
        EsperarLinhas(nome, "marca", 1);

        // O exec herda o console; o resultado volta pelo código de saída.
        var codigo = servico.ExecutarNoConteiner(nome, $"[ \"$(hostname)\" = \"{nome}\" ] && grep -q dentro /marca");

        Assert.Equal(0, codigo);
        servico.Parar(nome);
        dono.Wait(TimeSpan.FromSeconds(10));
    }

    [FatoLinux]
    public void StopNaoDeixaProcessosNoHost()
    {
        var nome = Nome();
        var marcador = $"sleep 6{Random.Shared.Next(100, 999)}";
        servico.Criar(nome, "alpine", $"{marcador} & {marcador}");
        var dono = Task.Run(() => servico.Iniciar(nome));
        EsperarExecucao(nome);
        Thread.Sleep(300);

        servico.Parar(nome);
        dono.Wait(TimeSpan.FromSeconds(10));

        var sobras = Directory.EnumerateDirectories("/proc")
            .Select(d => Path.Combine(d, "cmdline"))
            .Where(File.Exists)
            .Count(arquivo =>
            {
                try
                {
                    return File.ReadAllText(arquivo).Replace('\0', ' ').Trim() == marcador;
                }
                catch (IOException)
                {
                    return false;
                }
            });
        Assert.Equal(0, sobras);
    }

    [FatoLinux]
    public void LimitesSaoRecusadosEmContainerLinux()
    {
        var opcoes = new OpcoesConteiner { Limites = new LimitesRecursos { ProcessosMaximos = 5 } };

        Assert.Throws<ArgumentException>(() => servico.Criar(Nome(), "alpine", "true", opcoes));
    }

    [FatoLinux]
    public void ImagemWindowsNaoRodaNoLinux()
    {
        var nome = Nome();
        servico.Criar(nome, "scratch", "echo oi");

        Assert.Throws<InvalidOperationException>(() => servico.Iniciar(nome));
        Assert.Equal(EstadoConteiner.Criado, servico.Obter(nome).Estado);
    }

    [FatoLinux]
    public void RemoverApagaORootFs()
    {
        var conteiner = servico.Executar(Nome(), "alpine", "true");

        servico.Remover(conteiner.Nome);

        Assert.False(Directory.Exists(conteiner.CaminhoRootFs));
    }

    private static string Nome() => "t" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Espera o comando do container chegar ao ponto de ter escrito no arquivo.</summary>
    private void EsperarLinhas(string nome, string arquivo, int quantidade)
    {
        var caminho = Path.Combine(servico.Obter(nome).CaminhoRootFs, arquivo);
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(caminho) || File.ReadAllLines(caminho).Length < quantidade)
        {
            Assert.True(DateTime.UtcNow < limite, $"'{arquivo}' nao chegou a {quantidade} linha(s).");
            Thread.Sleep(20);
        }
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
}
