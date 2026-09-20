# MiniDocker

MiniDocker é um primeiro protótipo didático de um gerenciador de containers em C#/.NET 8. Ele cria um diretório por container, executa comandos de forma síncrona e registra informações reais do processo do sistema operacional. **Este protótipo não implementa isolamento real**: o diretório do container é usado somente como `WorkingDirectory`.

## Entregas do projeto

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª entrega: MVP** | Implementada | Criação, execução síncrona, listagem, parada representada por stub e remoção de containers. Registra PID, estado, código de saída, memória residente e tempo de CPU em um arquivo JSON. |
| **2ª entrega** | Ideia futura | Evolução para permitir que containers continuem executando em segundo plano e que sua saída possa ser consultada posteriormente. |
| **3ª entrega** | Ideia futura | Evolução para trabalhar com imagens reutilizáveis, facilitando a criação de novos containers a partir de uma base. |

As entregas 2 e 3 são apenas direções gerais. Os detalhes de implementação serão definidos quando essas etapas forem desenvolvidas.

## Requisitos

- .NET 8 SDK

## Estrutura

```text
src/MiniDocker.Nucleo/   Modelos, repositório e serviço
src/MiniDocker.Cli/      Interface de linha de comando
tests/MiniDocker.Testes/ Testes unitários
```

## Build, execução e testes

```bash
dotnet build
dotnet test
dotnet run --project src/MiniDocker.Cli -- run c1 "echo hello"
dotnet run --project src/MiniDocker.Cli -- ps
dotnet run --project src/MiniDocker.Cli -- rm c1
```

Também é possível usar `stop <nome>`, que neste protótipo informa que não há processo em background para interromper.

## Dados

Os dados ficam em `~/.minidocker/containers.json`. Os diretórios usados como rootfs ficam em `~/.minidocker/containers/<nome>`.

## Conceitos de SO ilustrados

- O processo é a unidade de execução observada pelo container, incluindo PID, estado e código de saída.
- O registro persistido funciona como um PCB simplificado de cada processo/container.
- O ciclo de vida passa por criado, executando e finalizado.
- `WorkingSet64` representa a memória residente observada, e `TotalProcessorTime` representa o tempo de CPU usado.
- Isolamento real com namespaces, cgroups ou chroot não é implementado neste protótipo; isso fica como gancho para discussão em sala, não como funcionalidade escondida.