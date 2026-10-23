using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Repositorios;

public interface IRepositorioImagem
{
    IReadOnlyList<Imagem> ObterTodas();

    /// <summary>Busca por <c>nome:tag</c> já normalizado.</summary>
    Imagem? ObterPorReferencia(string referencia);

    /// <summary>
    /// Grava a imagem. Se já houver outra com a mesma referência, ela é
    /// substituída e devolvida, para que o chamador apague os arquivos dela.
    /// </summary>
    Imagem? Salvar(Imagem imagem);

    void Remover(string id);
}
