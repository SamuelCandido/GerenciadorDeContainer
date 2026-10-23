using System.Text.Json;

namespace MiniDocker.Nucleo.Repositorios;

/// <summary>
/// Lista de registros gravada num arquivo JSON e compartilhada entre processos.
///
/// Com containers em segundo plano, o CLI e o processo dono de cada container
/// (o shim) leem e alteram o mesmo arquivo ao mesmo tempo. Um <c>lock</c> do C#
/// só protege threads do mesmo processo; entre processos é preciso uma trava
/// que o SO conheça. Aqui ela é um arquivo aberto com <see cref="FileShare.None"/>:
/// enquanto um processo o mantém aberto, o SO recusa a abertura pelos outros.
/// É uma seção crítica entre processos, no mesmo espírito de um mutex nomeado.
/// </summary>
internal sealed class ArmazenamentoJson<T>
{
    private static readonly TimeSpan EsperaMaximaTrava = TimeSpan.FromSeconds(10);

    private readonly string caminhoArquivo;
    private readonly string caminhoTrava;
    private readonly object sincronizador = new();
    private readonly JsonSerializerOptions opcoesJson = new() { WriteIndented = true };

    public ArmazenamentoJson(string caminhoArquivo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminhoArquivo)!);
        this.caminhoArquivo = caminhoArquivo;
        caminhoTrava = caminhoArquivo + ".lock";
    }

    public List<T> Ler() => ComTrava(Carregar);

    /// <summary>
    /// Lê, aplica a alteração e grava, tudo dentro da trava: nenhum outro
    /// processo grava entre a leitura e a escrita. Se a alteração lançar
    /// exceção, nada é gravado.
    /// </summary>
    public TResultado Alterar<TResultado>(Func<List<T>, TResultado> alteracao)
    {
        return ComTrava(() =>
        {
            var itens = Carregar();
            var resultado = alteracao(itens);
            Gravar(itens);
            return resultado;
        });
    }

    private TResultado ComTrava<TResultado>(Func<TResultado> acao)
    {
        lock (sincronizador)
        {
            using var trava = AbrirTrava();
            return acao();
        }
    }

    private FileStream AbrirTrava()
    {
        var limite = DateTime.UtcNow + EsperaMaximaTrava;
        while (true)
        {
            try
            {
                return new FileStream(caminhoTrava, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                Thread.Sleep(15);
            }
        }
    }

    private List<T> Carregar()
    {
        if (!File.Exists(caminhoArquivo))
        {
            return [];
        }

        var conteudo = File.ReadAllText(caminhoArquivo);
        return string.IsNullOrWhiteSpace(conteudo)
            ? []
            : JsonSerializer.Deserialize<List<T>>(conteudo, opcoesJson) ?? [];
    }

    private void Gravar(List<T> itens)
    {
        // Grava num temporário e troca de uma vez, para que uma queda no meio
        // da escrita não deixe o arquivo truncado.
        var temporario = caminhoArquivo + ".tmp";
        File.WriteAllText(temporario, JsonSerializer.Serialize(itens, opcoesJson));
        File.Move(temporario, caminhoArquivo, overwrite: true);
    }
}
