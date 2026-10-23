using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public class RepositorioImagemArquivo : IRepositorioImagem
{
    private readonly ArmazenamentoJson<Imagem> armazenamento;

    public RepositorioImagemArquivo(string? caminhoBase = null)
    {
        armazenamento = new ArmazenamentoJson<Imagem>(
            Path.Combine(Workspace.Raiz(caminhoBase), "images.json"));
    }

    public IReadOnlyList<Imagem> ObterTodas() => armazenamento.Ler().AsReadOnly();

    public Imagem? ObterPorReferencia(string referencia) =>
        armazenamento.Ler().FirstOrDefault(i => i.Referencia == referencia);

    public Imagem? Salvar(Imagem imagem)
    {
        return armazenamento.Alterar(imagens =>
        {
            var substituida = imagens.FirstOrDefault(i =>
                i.Referencia == imagem.Referencia && i.Id != imagem.Id);
            imagens.RemoveAll(i => i.Id == imagem.Id || i.Referencia == imagem.Referencia);
            imagens.Add(imagem);
            return substituida;
        });
    }

    public void Remover(string id)
    {
        armazenamento.Alterar(imagens => imagens.RemoveAll(i => i.Id == id));
    }
}
