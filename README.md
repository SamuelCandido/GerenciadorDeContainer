# MiniDocker

Gerenciador de containers didático em C#/.NET 10 para Windows, feito para a disciplina de Sistemas Operacionais.

Cada container é um **grupo de processos** criado dentro de um [Job Object](https://learn.microsoft.com/windows/win32/procthread/job-objects) do Windows, com restrições impostas pelo kernel e um ambiente próprio, separado do host.

> **O isolamento é parcial — e não é simulado.** As restrições implementadas são aplicadas pelo kernel do Windows: quando o container tenta usar um recurso bloqueado, quem nega é o sistema operacional. O que **não** está isolado, e o motivo técnico de cada caso, está em [Isolamento](#isolamento). A explicação conceitual completa está em [docs/ETAPA-1.md](docs/ETAPA-1.md) e o passeio pelo código em [docs/CODIGO.md](docs/CODIGO.md).

## Índice

- [Requisitos](#requisitos)
- [Início rápido](#início-rápido)
- [Comandos](#comandos)
- [Como funciona](#como-funciona)
- [Isolamento](#isolamento)
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
| `run <nome> <comando>` | Cria o container e executa o comando dentro dele |
| `ps` | Lista os containers registrados com estado e contabilidade |
| `stop <nome>` | *Stub nesta entrega* — a execução é síncrona, não há processo em segundo plano |
| `rm <nome>` | Remove o registro e o diretório do container |

Limites de memória, CPU e número de processos chegam na 2ª entrega.

## Como funciona

```text
run c1 "comando"
    │
    ├─ 1. cria o diretório rootfs do container
    ├─ 2. cria o Job Object e aplica as restrições    ← kernel passa a impor
    ├─ 3. monta um ambiente mínimo, sem herdar o host
    ├─ 4. inicia o cmd.exe com WorkingDirectory=rootfs
    ├─ 5. atribui o processo ao Job Object            ← descendentes herdam
    ├─ 6. aguarda o término
    ├─ 7. lê a contabilidade do grupo                 ← sobrevive ao término
    └─ 8. fecha o Job Object                          ← mata o que sobrou
```

O passo 5 é o que transforma um processo solto em container: a partir dele, **todo processo criado pelo comando nasce dentro do mesmo Job Object** e herda as mesmas restrições. Nenhum neto escapa.

O passo 7 só é confiável por causa de uma propriedade do Job Object: a contabilidade **sobrevive ao término dos processos**. Ler `Process.WorkingSet64` exigiria pegar o processo ainda vivo, o que é uma corrida contra comandos rápidos.

### Estados

`Criado` → `Executando` → `Finalizado`

### Dados persistidos

Ficam em `%USERPROFILE%\.minidocker\containers.json`, e o rootfs de cada container em `%USERPROFILE%\.minidocker\containers\<nome>`.

Cada registro funciona como um PCB simplificado: PID, estado, código de saída e a contabilidade do grupo — pico de memória, tempo de CPU, total de processos criados e falhas de página.

## Isolamento

Esta é a parte central do trabalho, então vale ser preciso sobre onde está a fronteira.

### Imposto pelo kernel

Tudo abaixo é aplicado pelo Windows. O container recebe acesso negado ao tentar usar estes recursos — não há verificação em C#.

| Restrição | Efeito |
|---|---|
| `UILIMIT_GLOBALATOMS` | O grupo recebe uma **tabela de atoms global própria** — um namespace privado |
| `UILIMIT_HANDLES` | Não alcança handles de janela de processos de fora do grupo |
| `UILIMIT_READCLIPBOARD` / `WRITECLIPBOARD` | Não lê nem escreve na área de transferência do host |
| `UILIMIT_DESKTOP` | Não cria nem troca de desktop |
| `UILIMIT_SYSTEMPARAMETERS` / `DISPLAYSETTINGS` | Não altera configurações do sistema |
| `UILIMIT_EXITWINDOWS` | Não desliga nem reinicia o Windows |
| Ambiente próprio | Não herda nenhuma variável do host; `TEMP`/`TMP` apontam para o rootfs |
| `LIMIT_KILL_ON_JOB_CLOSE` | Nenhum processo sobrevive ao fim do container |

### Não implementado — 2ª entrega

| Recurso | O que falta |
|---|---|
| Sistema de arquivos | Confinar a escrita ao rootfs exige `CreateRestrictedToken` + `CreateProcessAsUser`. Uma ACL sozinha não resolve: sem token restrito, o container roda com a **sua** identidade, então tem os mesmos direitos que você. |
| Privilégios | Mesmo mecanismo — derrubar privilégios exige o token restrito. |

### Fora de alcance

| Recurso | Por quê |
|---|---|
| Namespace de processos | O container enxerga o host via `tasklist`. Filtrar isso exige **Server Silos**, APIs não documentadas usadas pelo Host Compute System — seria embrulhar o runtime da Microsoft em vez de construir o próprio. |
| Rede | Mesma pilha e mesmas portas do host, pelo mesmo motivo. |

No Linux essa divisão é mais clara: **isolamento** vem de namespaces e **controle de recursos** vem de cgroups. O Job Object do Windows corresponde aos cgroups e cobre só uma parte dos namespaces — a do subsistema de janelas.

### Como verificar

```bat
:: o ambiente do host nao entra no container
set SEGREDO_DO_HOST=senha-123
minidocker run amb "echo [%SEGREDO_DO_HOST%]"
:: imprime [%SEGREDO_DO_HOST%] literal, porque a variavel nao existe la dentro

:: nenhum processo sobra: o ping morre junto com o container
minidocker run orfao "start /b ping -n 100 127.0.0.1"

:: o que ainda NAO esta isolado, honestamente
minidocker run sonda "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

## Estrutura do projeto

```text
src/MiniDocker.Nucleo/
  Modelos/        Conteiner, EstadoConteiner, LimitesRecursos
  Interop/        P/Invoke da API de Job Objects do Windows
  Repositorios/   Persistência em JSON
  Servicos/       Ciclo de vida do container
src/MiniDocker.Cli/      Interface de linha de comando
tests/MiniDocker.Testes/ Testes unitários
docs/ETAPA-1.md          Conceitos de SO aplicados nesta entrega
docs/CODIGO.md           Passeio pelo código, arquivo por arquivo
```

## Entregas

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª — isolamento e ciclo de vida** | Implementada | O container vira um grupo de processos num Job Object, com restrições de interface impostas pelo kernel, tabela de atoms própria, ambiente isolado do host e encerramento sem deixar órfãos. Criação, execução, listagem, remoção e contabilidade do grupo. |
| **2ª — confinamento e execução em segundo plano** | Planejada | Token restrito (`CreateRestrictedToken` + `CreateProcessAsUser`) confinando a escrita ao rootfs; limites de memória, CPU e número de processos; `stop` real via `TerminateJobObject`; captura de `stdout`/`stderr` em log. |
| **3ª — imagens reutilizáveis** | Planejada | Um rootfs base copiado para cada novo container. |
