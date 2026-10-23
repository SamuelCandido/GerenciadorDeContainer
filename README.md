# MiniDocker

Gerenciador de containers didático em C#/.NET 10 para Windows, feito para a disciplina de Sistemas Operacionais.

Cada container é um **grupo de processos** criado dentro de um [Job Object](https://learn.microsoft.com/windows/win32/procthread/job-objects) do Windows, com restrições impostas pelo kernel, ambiente próprio, sistema de arquivos copiado de uma **imagem** e ciclo de vida completo: criar, iniciar, parar, reiniciar, executar comandos dentro, ver logs e remover.

> **O isolamento é parcial — e não é simulado.** As restrições implementadas são aplicadas pelo kernel do Windows: quando o container tenta usar um recurso bloqueado, quem nega é o sistema operacional. O que **não** está isolado, e o motivo técnico de cada caso, está em [Isolamento](#isolamento). Os conceitos estão em [docs/ETAPA-1.md](docs/ETAPA-1.md) e [docs/ETAPA-2.md](docs/ETAPA-2.md), e o passeio pelo código em [docs/CODIGO.md](docs/CODIGO.md).

## Índice

- [Requisitos](#requisitos)
- [Início rápido](#início-rápido)
- [Comandos](#comandos)
- [Imagens e o MiniDockerfile](#imagens-e-o-minidockerfile)
- [Imagens Linux: alpine, ubuntu](#imagens-linux-alpine-ubuntu)
- [Como funciona](#como-funciona)
- [Isolamento](#isolamento)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Entregas](#entregas)

## Requisitos

- .NET 10 SDK
- E, conforme o tipo de imagem:

| Imagem | Host Windows | Host Linux |
|---|---|---|
| **Windows** (`scratch`, `build`) | ✔ `cmd.exe` num Job Object | ✘ |
| **Linux** (`alpine`, `ubuntu`… via `pull`) | ✔ com WSL2 instalado | ✔ namespaces + `chroot`, sem sudo |

Os testes que dependem de uma plataforma são **ignorados** na outra, em vez de falharem. Os testes do runtime Linux baixam o `alpine` do Docker Hub, então precisam de internet.

## Início rápido

```bat
dotnet build
dotnet test

:: um alias para encurtar os exemplos
doskey minidocker=dotnet run --project src\MiniDocker.Cli -- $*

:: container descartavel, anexado ao terminal
minidocker run --rm scratch echo ola
```

### O gato e o cachorro

Para apresentar, siga o roteiro em [exemplos/README.md](exemplos/README.md): cada passo traz o comando, a saída esperada e o que explicar.

Há dois exemplos, um de cada tipo de container:

| Imagem | Tipo | O que faz |
|---|---|---|
| [`exemplos/gato`](exemplos/gato) | **Roda e morre** | Mia uma vez e termina — o container fica `Finalizado (0)`. Como um script, um job, uma migração de banco |
| [`exemplos/cachorro`](exemplos/cachorro) | **Roda em loop** | Late a cada 2 s até alguém dar `stop`. Como um servidor web, um banco de dados |

```bat
minidocker build -t gato exemplos\gato
minidocker run --name mingau gato                :: mia e termina; o terminal fica livre
minidocker ps -a                                 :: mingau: Finalizado (0)
```

O cachorro também guarda, **num arquivo dentro do próprio container**, quantas vezes ele já foi iniciado. É isso que mostra que o `start` volta o mesmo container:

```bat
minidocker build -t cachorro exemplos\cachorro   :: 1. a imagem: o molde
minidocker run -d --name rex cachorro            :: 2. o container rex nasce da imagem e roda
minidocker logs rex                              ::    "Rex acordou! ... iniciado 1 vez(es)"

minidocker stop rex                              :: 3. os processos morrem...
minidocker ps -a                                 ::    ...mas o rex continua existindo: "Parado (137)"

minidocker start rex                             :: 4. o MESMO container volta, sem novo run
minidocker logs rex                              ::    "... iniciado 2 vez(es)": o arquivo sobreviveu

minidocker run -d --name bidu -e NOME=Bidu cachorro  :: 5. outro container da mesma imagem
minidocker logs bidu                                 ::    "Bidu acordou! ... iniciado 1 vez(es)"

minidocker rm -f rex bidu                        :: 6. agora sim deixam de existir
```

Os três conceitos que a demonstração separa:

| | O que é | Analogia |
|---|---|---|
| **Imagem** | Molde só-leitura: arquivos + comando padrão | A receita do bolo |
| **Container** | Uma cópia da imagem com estado próprio (seus arquivos, seu log) | Um bolo feito da receita |
| **Processo** | O programa rodando dentro do container | O bolo no forno |

E o que cada comando faz com eles:

| Comando | Processos | Arquivos do container | Container |
|---|---|---|---|
| `stop` | Morrem | Ficam | Continua existindo, `Parado` |
| `start` | Nascem de novo, do zero | Os mesmos de antes | O mesmo |
| `rm` | — | Apagados | Deixa de existir |
| `run` | Nascem | Cópia nova da imagem | Um container **novo** |

## Comandos

A sintaxe segue a do Docker. Containers e imagens podem ser referidos pelo nome ou por um prefixo do id.

### Containers

| Comando | O que faz |
|---|---|
| `run [opções] IMAGEM [COMANDO]` | Cria e executa. Sem `COMANDO`, usa o `CMD` da imagem |
| `create [opções] IMAGEM [COMANDO]` | Cria sem executar (estado `Criado`) |
| `start [-a] CONTAINER...` | Inicia em segundo plano; `-a` anexa ao terminal |
| `stop CONTAINER...` | Encerra o grupo inteiro (`TerminateJobObject`); código de saída 137 |
| `restart CONTAINER...` | `stop` + `start` |
| `rm [-f] CONTAINER...` | Remove registro, rootfs e log; `-f` para antes se estiver rodando |
| `ps [-a] [-q]` | Lista os em execução; `-a` inclui os parados |
| `logs [-f] CONTAINER` | Saída do container; `-f` acompanha ao vivo |
| `exec CONTAINER COMANDO` | Executa mais um processo **dentro do mesmo Job Object** |
| `stats [CONTAINER...]` | CPU %, pico de memória e processos, lidos ao vivo do Job Object |
| `inspect NOME...` | Registro completo em JSON (container ou imagem) |

Opções de `run`/`create`:

| Opção | Efeito | Mecanismo |
|---|---|---|
| `-d` | Segundo plano; imprime o id | Processo shim (ver [Como funciona](#como-funciona)) |
| `-it` | Interativo, ligado ao terminal (sem log) | Processo herda o console |
| `--name NOME` | Nome do container; sem ele, um nome é gerado | |
| `-e CHAVE=valor` | Variável de ambiente; `-e CHAVE` copia a do host | Sobrescreve o `ENV` da imagem |
| `-m 256m` | Limite de memória | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` / `JOB_MEMORY` |
| `--cpus 0.5` | Limite de CPU, em núcleos | `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` |
| `--pids-limit 10` | Máximo de processos vivos | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` |
| `--rm` | Remove o container ao terminar | |

### Imagens

| Comando | O que faz |
|---|---|
| `build -t NOME[:TAG] [-f ARQUIVO] [CONTEXTO]` | Constrói a partir do `MiniDockerfile` do contexto |
| `images [-q]` | Lista as imagens |
| `rmi IMAGEM...` | Remove imagens |
| `commit CONTAINER NOME[:TAG]` | Congela o rootfs atual de um container numa imagem |
| `save -o ARQUIVO.zip IMAGEM` | Exporta para um zip, para levar a outra máquina |
| `load -i ARQUIVO.zip` | Importa |

A imagem `scratch` é embutida e vazia.

## Imagens e o MiniDockerfile

Uma imagem é um **rootfs base** mais os padrões de execução (`CMD` e `ENV`). Criar um container **copia** o rootfs da imagem para o container: cada container altera a própria cópia sem afetar a imagem nem os vizinhos.

O `MiniDockerfile` é um subconjunto do Dockerfile:

```dockerfile
FROM scratch                       # ou outra imagem local
COPY cachorro.bat .                # do contexto de build para o rootfs
ENV NOME=Rex                       # herdada pelos containers
RUN echo construida > build.txt    # roda num container de build isolado
CMD cachorro.bat                   # comando padrão do run
```

O `RUN` não roda no host: roda num **container temporário**, com o mesmo Job Object, as mesmas restrições e o mesmo ambiente isolado, tendo o rootfs em construção como diretório de trabalho. O que ele gravar ali vira parte da imagem. `COPY` não aceita caminhos que saiam do contexto (`..\`).

O Docker evita a cópia usando **camadas** e um sistema de arquivos de união (overlayfs no Linux, wcifs no Windows): a imagem é só-leitura e cada container ganha uma camada gravável por cima. No MiniDocker as camadas ficam "achatadas" — cada `FROM` e cada container é uma cópia completa. O custo é disco e tempo de criação; o comportamento observável (o container não altera a imagem) é o mesmo.

## Imagens Linux: alpine, ubuntu

```bat
minidocker pull alpine                 :: baixa do Docker Hub
minidocker run alpine cat /etc/os-release
minidocker run -d --name web alpine sh -c "while true; do date; sleep 2; done"
minidocker exec web ps                 :: so os processos do container
minidocker stop web & minidocker start web & minidocker rm -f web
```

O `run` de uma imagem que não existe localmente baixa sozinho, como no Docker. Funcionam alpine, ubuntu, debian, busybox e outras imagens do Docker Hub.

### Um container não tem kernel próprio

Uma imagem Linux traz programas que fazem **chamadas de sistema do Linux**. Como o container usa o kernel do host, quem roda essa imagem precisa de um kernel Linux por baixo:

| Host | Como o MiniDocker roda uma imagem Linux | Isolamento |
|---|---|---|
| **Linux** | Direto no kernel do host, com `unshare` + `chroot` | **Namespaces de verdade**: o container vê só os próprios processos (PID 1 é dele), tem hostname, `/proc` e montagens próprios, e é "root" sem ser root no host |
| **Windows** | Numa distro do **WSL2**, uma VM leve com kernel Linux — o mesmo que o Docker Desktop faz | O da VM |

No Linux, o paralelo com o Windows é direto:

| | Windows | Linux |
|---|---|---|
| Grupo de processos | Job Object | Namespace de PID |
| Matar o grupo inteiro | `TerminateJobObject` | `SIGKILL` no init — o kernel derruba o namespace |
| Nada sobra quando o dono morre | `KILL_ON_JOB_CLOSE` | `unshare --kill-child` |
| Saber se o container está vivo | O nome do Job Object existe | O processo dono segura uma trava de arquivo |
| Entrar no container (`exec`) | `AssignProcessToJobObject` | `nsenter` nos mesmos namespaces |
| Ver só os próprios processos | ✘ (exigiria Server Silos) | ✔ |
| Limites e `stats` | ✔ | ✘ — seria cgroups, deixado de fora |

### O que ficou reduzido

- Limites (`-m`, `--cpus`, `--pids-limit`) e `stats` só para imagens Windows.
- `build`, `commit` e `save` só para imagens Windows; imagens Linux vêm do `pull`.
- As camadas da imagem são juntadas num `.tar` só; arquivos que uma camada de cima apaga (`.wh.*`) são ignorados.
- Sem rede própria: o container usa a rede do host.

## Como funciona

```text
minidocker run -d cachorro
    │
    ├─ create: copia o rootfs da imagem, grava o registro (Criado)
    │
    └─ lança o shim em segundo plano e sai ─────────┐
                                                    ▼
                          minidocker __shim <nome>   (dono do container)
                              ├─ 1. cria o Job Object com nome    ← trava: um start por vez
                              ├─ 2. aplica restrições e limites   ← kernel passa a impor
                              ├─ 3. marca Executando
                              ├─ 4. inicia o cmd.exe no rootfs, com ambiente mínimo
                              ├─ 5. atribui o processo ao job     ← descendentes herdam
                              ├─ 6. grava stdout/stderr no log
                              ├─ 7. processo principal termina    ← encerra os netos
                              ├─ 8. lê a contabilidade do grupo
                              └─ 9. marca Finalizado (ou Parado, se foi um stop)
```

O **shim** cumpre o papel do `containerd-shim` do Docker: é ele, e não o CLI, que segura o Job Object, então o terminal pode ser fechado sem matar o container. No modo anexado (sem `-d`), o próprio CLI é o dono, e Ctrl+C equivale a `stop`.

O Job Object recebe um **nome** no namespace da sessão (`Local\MiniDocker-<id>`). É assim que outros processos encontram o container:

| Comando | O que faz com o nome |
|---|---|
| `stop` | `OpenJobObject` + `TerminateJobObject` — mata o grupo inteiro de uma vez |
| `exec` | `OpenJobObject` + `AssignProcessToJobObject` — o processo novo herda limites e restrições |
| `stats` | `OpenJobObject` + `QueryInformationJobObject` — contabilidade ao vivo |
| `ps` | Se o registro diz `Executando` mas o nome não existe mais no kernel, o dono morreu: o registro é corrigido |

### Estados

```text
           start                  processo termina sozinho
 Criado ─────────► Executando ────────────────────────────► Finalizado
                     │   ▲                                      │
                stop │   │ start / restart                      │
                     ▼   │                                      │
                   Parado ◄─────────────────────────────────────┘ (start também vale daqui)
```

Qualquer estado aceita `rm`, menos `Executando`, que exige `rm -f`.

### Dados persistidos

Em `%USERPROFILE%\.minidocker\`:

| Caminho | Conteúdo |
|---|---|
| `containers.json` | Registro de cada container — um PCB simplificado |
| `containers\<nome>\` | Rootfs de cada container |
| `logs\<id>.log` | Saída de cada container; acumula entre restarts |
| `images.json` | Registro das imagens |
| `images\<id>\rootfs\` | Rootfs de cada imagem |

O CLI e os shims gravam no mesmo JSON ao mesmo tempo. A exclusão mútua entre processos é feita com um arquivo de trava aberto com `FileShare.None`, e cada gravação lê o registro mais recente antes de alterar.

## Isolamento

### Imposto pelo kernel

| Restrição | Efeito |
|---|---|
| `UILIMIT_GLOBALATOMS` | O grupo recebe uma **tabela de atoms global própria** — um namespace privado |
| `UILIMIT_HANDLES` | Não alcança handles de janela de processos de fora do grupo |
| `UILIMIT_READCLIPBOARD` / `WRITECLIPBOARD` | Não lê nem escreve na área de transferência do host |
| `UILIMIT_DESKTOP` | Não cria nem troca de desktop |
| `UILIMIT_SYSTEMPARAMETERS` / `DISPLAYSETTINGS` | Não altera configurações do sistema |
| `UILIMIT_EXITWINDOWS` | Não desliga nem reinicia o Windows |
| Limites de memória, CPU e processos | Ultrapassar faz a alocação ou a criação de processo falhar |
| Ambiente próprio | Não herda nenhuma variável do host; só entra o que a imagem ou o `-e` definem |
| `LIMIT_KILL_ON_JOB_CLOSE` | Nenhum processo sobrevive ao fim do container |

### Não implementado

| Recurso | O que falta |
|---|---|
| Sistema de arquivos | O rootfs é só o diretório de trabalho: o container ainda lê e escreve fora dele. Confinar a escrita exige um token restrito (`CreateRestrictedToken` ou nível de integridade baixo + `CreateProcessAsUser`). |
| Privilégios | Mesmo mecanismo. |
| Imagem com userland própria | O container roda o `cmd.exe` do host. A imagem empacota os **arquivos da aplicação**, não um Windows inteiro. |

### Fora de alcance

| Recurso | Por quê |
|---|---|
| Namespace de processos (containers Windows) | O container enxerga o host via `tasklist`. Filtrar isso exige **Server Silos**, APIs não documentadas usadas pelo Host Compute System — seria embrulhar o runtime da Microsoft em vez de construir o próprio. |
| Rede | Mesma pilha e mesmas portas do host, pelo mesmo motivo. |

No Linux essa divisão é mais clara: **isolamento** vem de namespaces e **controle de recursos** vem de cgroups. O Job Object do Windows corresponde aos cgroups e cobre só uma parte dos namespaces — a do subsistema de janelas.

### Como verificar

```bat
:: o ambiente do host nao entra no container
:: (o set roda dentro do container; um echo %VAR% seria expandido pelo cmd do host antes)
set SEGREDO_DO_HOST=senha-123
minidocker run --rm scratch set
:: SEGREDO_DO_HOST nao aparece
minidocker run --rm -e SEGREDO_DO_HOST scratch set
:: agora aparece: entrou porque foi pedido explicitamente

:: o stop mata o grupo inteiro, netos inclusive
minidocker run -d --name orfao scratch "start /b ping -t 127.0.0.1 & ping -t 127.0.0.1"
minidocker stats orfao
minidocker stop orfao

:: o exec entra no mesmo grupo e respeita o limite de processos
minidocker run -d --name lim --pids-limit 2 scratch ping -t 127.0.0.1
minidocker exec lim ping -n 1 127.0.0.1
:: falha: o grupo ja tem cmd.exe + ping, o terceiro processo e recusado pelo kernel

:: o que ainda NAO esta isolado, honestamente
minidocker run --rm scratch "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

## Estrutura do projeto

```text
src/MiniDocker.Nucleo/
  Modelos/        Conteiner, Imagem, EstadoConteiner, LimitesRecursos, OpcoesConteiner
  Interop/        Grupo de processos: Job Object (Windows) e namespace de PID (Linux)
  Repositorios/   Persistência em JSON com trava entre processos
  Servicos/       Ciclo de vida do container, imagens, build, pull
    Runtimes/     Como executar cada plataforma: Windows, Linux, WSL2
src/MiniDocker.Cli/      Interface de linha de comando e shim
tests/MiniDocker.Testes/ Testes unitários
exemplos/                Contextos de build de exemplo
docs/ETAPA-1.md          Conceitos: Job Objects, isolamento, contabilidade
docs/ETAPA-2.md          Conceitos: segundo plano, job com nome, imagens
docs/ETAPA-3.md          Plano da etapa final
exemplos/README.md       Roteiro de demonstração passo a passo
docs/CODIGO.md           Passeio pelo código, arquivo por arquivo
```

## Entregas

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª — isolamento e ciclo de vida** | Implementada | O container vira um grupo de processos num Job Object, com restrições de interface impostas pelo kernel, tabela de atoms própria, ambiente isolado do host e encerramento sem deixar órfãos. |
| **2ª — execução em segundo plano** | Implementada | Shim dono do container; Job Object com nome; `start`, `stop` real via `TerminateJobObject`, `restart`, `exec`, `logs`, `stats`; limites de memória, CPU e processos na CLI. |
| **3ª — imagens** | Implementada | `build` com MiniDockerfile (`RUN` em container isolado), `images`, `rmi`, `commit`, `save`/`load`; rootfs copiado da imagem para cada container. |
| **Final — controle fino e confinamento** | Planejada | Volumes, política de restart, eventos do kernel, criação suspensa e confinamento do sistema de arquivos — detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md). |
