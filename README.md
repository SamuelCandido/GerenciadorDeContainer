# MiniDocker

Gerenciador de containers didático em C#/.NET 10 para Windows, feito para a disciplina de Sistemas Operacionais.

Cada container é um **grupo de processos controlado pelo kernel** através de um [Job Object](https://learn.microsoft.com/windows/win32/procthread/job-objects): o MiniDocker cria o grupo, aplica limites de memória, de número de processos e de CPU, executa o comando via `cmd.exe` e contabiliza o consumo real do grupo inteiro.

> **Limitar recursos não é isolar.** O MiniDocker impõe limites de verdade, mas não isola sistema de arquivos, rede nem lista de processos. A seção [Isolamento e limites](#isolamento-e-limites) detalha a diferença, e [docs/ETAPA-1.md](docs/ETAPA-1.md) traz a explicação conceitual completa.

## Índice

- [Requisitos](#requisitos)
- [Início rápido](#início-rápido)
- [Comandos](#comandos)
- [Como funciona](#como-funciona)
- [Isolamento e limites](#isolamento-e-limites)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Entregas](#entregas)

## Requisitos

- Windows — os containers rodam via `cmd.exe /c` dentro de um Job Object
- .NET 10 SDK

O código compila em qualquer sistema operacional, mas só executa no Windows. Fora do Windows, os testes que dependem do `cmd.exe` e da API de Job Objects são **ignorados** em vez de falharem, o que permite desenvolver em outro sistema — mas a validação real precisa acontecer no Windows.

## Início rápido

```bat
dotnet build
dotnet test

dotnet run --project src\MiniDocker.Cli -- run c1 "echo ola"
dotnet run --project src\MiniDocker.Cli -- ps
dotnet run --project src\MiniDocker.Cli -- rm c1
```

## Comandos

| Comando | O que faz |
|---|---|
| `run <nome> [limites] <comando>` | Cria o container, aplica os limites e executa o comando |
| `ps` | Lista os containers registrados com estado e contabilidade |
| `stop <nome>` | *Stub nesta entrega* — a execução é síncrona, não há processo em segundo plano |
| `rm <nome>` | Remove o registro e o diretório do container |

### Limites de recursos

```bat
minidocker run <nome> [--memoria <valor>] [--processos <n>] [--cpu <1-100>] <comando>
```

| Opção | Efeito | Limite correspondente |
|---|---|---|
| `--memoria 128M` | Alocar além do teto falha. Aceita sufixos `K`, `M` e `G` | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` |
| `--processos 4` | Criar o 5º processo falha. Barra fork bombs | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` |
| `--cpu 50` | O grupo nunca passa de 50% de CPU | `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` |

```bat
minidocker run c1 --memoria 128M --processos 4 --cpu 50 "echo ola"
```

## Como funciona

```text
run c1 --memoria 128M "comando"
    │
    ├─ 1. cria o diretório rootfs do container
    ├─ 2. cria o Job Object e aplica os limites      ← kernel passa a impor
    ├─ 3. inicia o cmd.exe com WorkingDirectory=rootfs
    ├─ 4. atribui o processo ao Job Object           ← descendentes herdam
    ├─ 5. aguarda o término
    ├─ 6. lê a contabilidade do grupo                ← sobrevive ao término
    └─ 7. fecha o Job Object                         ← mata o que sobrou
```

O passo 4 é o que transforma um processo solto em container: a partir dele, **todo processo criado pelo comando nasce dentro do mesmo Job Object** e herda os mesmos limites. Não há como um neto escapar do teto.

O passo 6 só é confiável por causa de uma propriedade do Job Object: a contabilidade **sobrevive ao término dos processos**. Ler `Process.WorkingSet64` exigiria pegar o processo ainda vivo, o que é uma corrida contra comandos rápidos.

### Estados

`Criado` → `Executando` → `Finalizado`

### Dados persistidos

Ficam em `%USERPROFILE%\.minidocker\containers.json`, e o rootfs de cada container em `%USERPROFILE%\.minidocker\containers\<nome>`.

Cada registro funciona como um PCB simplificado: PID, estado, código de saída, limites aplicados e a contabilidade do grupo — pico de memória, tempo de CPU, total de processos criados e falhas de página.

## Isolamento e limites

Esta é a distinção central do trabalho, então vale ser explícito.

**O que é imposto pelo kernel:**

| Recurso | Como é imposto |
|---|---|
| Memória | Alocação acima do teto falha |
| Número de processos | Criação de processo acima do teto falha |
| CPU | O escalonador não deixa o grupo passar do percentual |
| Encerramento | `KILL_ON_JOB_CLOSE` mata o grupo inteiro, sem deixar órfãos |

**O que não é isolado:**

| Recurso | Consequência |
|---|---|
| Sistema de arquivos | `CaminhoRootFs` é só o diretório inicial; caminhos absolutos saem dele |
| Lista de processos | O container enxerga os processos do host via `tasklist` |
| Variáveis de ambiente | O ambiente do processo pai é herdado inteiro |
| Usuário e privilégios | Roda com as credenciais de quem chamou o MiniDocker |
| Rede | Mesma pilha de rede e mesmas portas do host |

A separação não é acidental. No Linux, **isolamento** vem de namespaces (PID, mount, rede) e **controle de recursos** vem de cgroups — dois mecanismos distintos. O Job Object do Windows é o equivalente aos cgroups, não aos namespaces. O análogo de namespaces no Windows são os Server Silos, usados pelos Windows Containers via Host Compute System; implementá-los significaria embrulhar o runtime da Microsoft em vez de construir o próprio, o que está fora do escopo do trabalho.

### Como verificar

```bat
:: fork bomb barrada no limite de 3 processos
minidocker run bomba --processos 3 "for /L %i in (1,1,50) do start /b cmd /c ping -n 5 127.0.0.1"

:: nenhum processo sobra: o ping morre junto com o container
minidocker run orfao "start /b ping -n 100 127.0.0.1"

:: teto de CPU observável no Gerenciador de Tarefas
minidocker run cpu --cpu 10 "for /L %i in (1,1,100000000) do rem"

:: ausência de isolamento: o container lê fora do rootfs e enxerga o host
minidocker run sonda "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

## Estrutura do projeto

```text
src/MiniDocker.Nucleo/
  Modelos/        Conteiner, EstadoConteiner, LimitesRecursos
  Interop/        P/Invoke da API de Job Objects do Windows
  Repositorios/   Persistência em JSON
  Servicos/       Ciclo de vida do container
src/MiniDocker.Cli/     Interface de linha de comando
tests/MiniDocker.Testes/ Testes unitários
docs/ETAPA-1.md          Conceitos de SO aplicados nesta entrega
docs/CODIGO.md           Passeio pelo código, arquivo por arquivo
```

## Entregas

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª — ciclo de vida e controle de recursos** | Implementada | Criação, execução, listagem e remoção. Cada container vira um Job Object com limites de memória, processos e CPU impostos pelo kernel, contabilidade exata do grupo e encerramento sem órfãos. |
| **2ª — execução em segundo plano** | Planejada | `stop` real via `TerminateJobObject`, captura de `stdout`/`stderr` em log, consulta ao estado de containers vivos e restrição de privilégios com `CreateRestrictedToken`. |
| **3ª — imagens reutilizáveis** | Planejada | Um rootfs base copiado para cada novo container, com restrição de acesso a arquivos por ACL de AppContainer. |
