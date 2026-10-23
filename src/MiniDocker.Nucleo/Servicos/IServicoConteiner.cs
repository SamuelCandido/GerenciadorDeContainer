using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Servicos;

public interface IServicoConteiner
{
    /// <summary>
    /// <c>docker create</c>: copia o rootfs da imagem e registra o container em
    /// <see cref="EstadoConteiner.Criado"/>, sem executar nada. Sem
    /// <paramref name="comando"/>, usa o <c>CMD</c> da imagem.
    /// </summary>
    Conteiner Criar(string? nome, string imagem, string? comando = null, OpcoesConteiner? opcoes = null);

    /// <summary>
    /// Executa o container até o processo principal terminar, neste processo.
    /// O CLI chama direto no modo anexado e por meio do shim no modo
    /// <c>-d</c>; nos dois casos quem chama vira o dono do Job Object.
    /// </summary>
    /// <param name="saida">Recebe cada linha de saída, além do log.</param>
    /// <param name="interativo">Liga o processo ao console de quem chamou (<c>-it</c>), sem log.</param>
    Conteiner Iniciar(string nomeOuId, Action<string>? saida = null, bool interativo = false);

    /// <summary><c>docker run</c> anexado: <see cref="Criar"/> seguido de <see cref="Iniciar"/>.</summary>
    Conteiner Executar(string? nome, string imagem, string? comando = null,
        OpcoesConteiner? opcoes = null, Action<string>? saida = null);

    /// <summary>Busca pelo nome ou por um prefixo do id, como o Docker.</summary>
    Conteiner Obter(string nomeOuId);

    IReadOnlyList<Conteiner> Listar();

    /// <summary>Encerra o grupo inteiro e espera o dono registrar o fim.</summary>
    Conteiner Parar(string nomeOuId);

    /// <param name="forcar">Para o container antes, se estiver em execução (<c>rm -f</c>).</param>
    void Remover(string nomeOuId, bool forcar = false);

    /// <summary><c>docker exec</c>: roda mais um processo dentro do grupo do container.</summary>
    int ExecutarNoConteiner(string nomeOuId, string comando);

    /// <summary>Contabilidade ao vivo do Job Object de um container em execução.</summary>
    ContabilidadeTrabalho Estatisticas(string nomeOuId);

    /// <summary>Envia o log para <paramref name="saida"/>; com <paramref name="seguir"/>, acompanha até o container terminar.</summary>
    void LerLogs(string nomeOuId, bool seguir, Action<string> saida, CancellationToken cancelamento = default);
}
