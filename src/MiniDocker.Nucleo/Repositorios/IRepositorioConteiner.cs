using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public interface IRepositorioConteiner
{
    IReadOnlyList<Conteiner> ObterTodos();
    Conteiner? ObterPorNome(string nome);
    void Salvar(Conteiner conteiner);

    /// <summary>
    /// Lê o registro mais recente, aplica a alteração e grava, sem que outro
    /// processo grave no meio. É o que permite ao CLI e ao shim mexerem no
    /// mesmo container sem um sobrescrever o outro.
    /// </summary>
    Conteiner Atualizar(string nome, Action<Conteiner> alteracao);

    void Remover(string nome);
}
