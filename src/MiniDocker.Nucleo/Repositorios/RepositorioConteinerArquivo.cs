using System.Text.Json;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public class RepositorioConteinerArquivo : IRepositorioConteiner
{
    private readonly string caminhoArquivo;
    private readonly object sincronizador = new();
    private readonly JsonSerializerOptions opcoesJson = new() { WriteIndented = true };

    public RepositorioConteinerArquivo(string? caminhoBase = null)
    {
        var baseRepositorio = caminhoBase ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minidocker");
        Directory.CreateDirectory(baseRepositorio);
        caminhoArquivo = Path.Combine(baseRepositorio, "containers.json");
    }

    public IReadOnlyList<Conteiner> ObterTodos()
    {
        lock (sincronizador)
        {
            return Carregar().AsReadOnly();
        }
    }

    public Conteiner? ObterPorNome(string nome)
    {
        lock (sincronizador)
        {
            return Carregar().FirstOrDefault(c =>
                string.Equals(c.Nome, nome, StringComparison.Ordinal));
        }
    }

    public void Salvar(Conteiner conteiner)
    {
        lock (sincronizador)
        {
            var conteineres = Carregar();
            var indice = conteineres.FindIndex(c =>
                string.Equals(c.Nome, conteiner.Nome, StringComparison.Ordinal));

            if (indice >= 0)
            {
                conteineres[indice] = conteiner;
            }
            else
            {
                conteineres.Add(conteiner);
            }

            File.WriteAllText(caminhoArquivo,
                JsonSerializer.Serialize(conteineres, opcoesJson));
        }
    }

    public void Remover(string nome)
    {
        lock (sincronizador)
        {
            var conteineres = Carregar();
            conteineres.RemoveAll(c => string.Equals(c.Nome, nome, StringComparison.Ordinal));
            File.WriteAllText(caminhoArquivo,
                JsonSerializer.Serialize(conteineres, opcoesJson));
        }
    }

    private List<Conteiner> Carregar()
    {
        if (!File.Exists(caminhoArquivo))
        {
            return [];
        }

        var conteudo = File.ReadAllText(caminhoArquivo);
        return string.IsNullOrWhiteSpace(conteudo)
            ? []
            : JsonSerializer.Deserialize<List<Conteiner>>(conteudo) ?? [];
    }
}