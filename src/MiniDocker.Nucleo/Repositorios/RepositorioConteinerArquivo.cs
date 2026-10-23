using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public class RepositorioConteinerArquivo : IRepositorioConteiner
{
    private readonly ArmazenamentoJson<Conteiner> armazenamento;

    public RepositorioConteinerArquivo(string? caminhoBase = null)
    {
        armazenamento = new ArmazenamentoJson<Conteiner>(
            Path.Combine(Workspace.Raiz(caminhoBase), "containers.json"));
    }

    public IReadOnlyList<Conteiner> ObterTodos() => armazenamento.Ler().AsReadOnly();

    public Conteiner? ObterPorNome(string nome) =>
        armazenamento.Ler().FirstOrDefault(c => MesmoNome(c, nome));

    public void Salvar(Conteiner conteiner)
    {
        armazenamento.Alterar(conteineres =>
        {
            var indice = conteineres.FindIndex(c => MesmoNome(c, conteiner.Nome));
            if (indice >= 0)
            {
                conteineres[indice] = conteiner;
            }
            else
            {
                conteineres.Add(conteiner);
            }

            return conteiner;
        });
    }

    public Conteiner Atualizar(string nome, Action<Conteiner> alteracao)
    {
        return armazenamento.Alterar(conteineres =>
        {
            var conteiner = conteineres.FirstOrDefault(c => MesmoNome(c, nome))
                ?? throw new InvalidOperationException($"Container '{nome}' nao existe.");
            alteracao(conteiner);
            return conteiner;
        });
    }

    public void Remover(string nome)
    {
        armazenamento.Alterar(conteineres => conteineres.RemoveAll(c => MesmoNome(c, nome)));
    }

    private static bool MesmoNome(Conteiner conteiner, string nome) =>
        string.Equals(conteiner.Nome, nome, StringComparison.Ordinal);
}
