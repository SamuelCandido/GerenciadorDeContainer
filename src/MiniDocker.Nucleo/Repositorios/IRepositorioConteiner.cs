using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public interface IRepositorioConteiner
{
    IReadOnlyList<Conteiner> ObterTodos();
    Conteiner? ObterPorNome(string nome);
    void Salvar(Conteiner conteiner);
    void Remover(string nome);
}