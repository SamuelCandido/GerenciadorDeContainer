using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

public interface IServicoConteiner
{
    Conteiner Executar(string nome, string comando);
    IReadOnlyList<Conteiner> Listar();
    void Parar(string nome);
    void Remover(string nome);
}