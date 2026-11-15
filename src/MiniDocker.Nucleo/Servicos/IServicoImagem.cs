using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

public interface IServicoImagem
{
    /// <summary>
    /// <c>docker build -t referencia pasta</c>. Sem MiniDockerfile na pasta, ela
    /// inteira vira a imagem e o comando é o único programa encontrado nela.
    /// </summary>
    /// <param name="comando"><c>--cmd</c>: o comando padrão, por cima de qualquer <c>CMD</c>.</param>
    /// <param name="ambiente"><c>-e</c>: variáveis, por cima de qualquer <c>ENV</c>.</param>
    Imagem Construir(
        string contexto,
        string referencia,
        string? arquivo = null,
        Action<string>? saida = null,
        string? comando = null,
        IReadOnlyDictionary<string, string>? ambiente = null);

    IReadOnlyList<Imagem> Listar();

    /// <summary>Busca por <c>nome[:tag]</c> ou prefixo do id; <c>scratch</c> é a imagem vazia.</summary>
    Imagem Obter(string referenciaOuId);

    void Remover(string referenciaOuId);

    /// <summary><c>docker commit</c>: congela o rootfs de um container numa imagem nova.</summary>
    Imagem Commit(Conteiner conteiner, string referencia);

    /// <summary><c>docker save</c>: empacota a imagem num zip.</summary>
    void Exportar(string referenciaOuId, string arquivoZip);

    /// <summary><c>docker load</c>.</summary>
    Imagem Importar(string arquivoZip);
}
