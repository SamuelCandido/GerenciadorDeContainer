# MiniDocker

Gerenciador de containers didático em C#/.NET 10 para Windows, feito para a disciplina de Sistemas Operacionais.

Cada container é um **grupo de processos** criado dentro de um [Job Object](https://learn.microsoft.com/windows/win32/procthread/job-objects) do Windows, com restrições impostas pelo kernel, ambiente próprio, sistema de arquivos copiado de uma **imagem**, **escrita confinada** ao próprio rootfs, **volumes**, **portas publicadas**, **réplicas**, **detecção de erros pelo kernel** e ciclo de vida completo: criar, iniciar, parar, reiniciar, executar comandos dentro, ver logs e remover. Um arquivo **compose** sobe um sistema inteiro com um comando.

> **O isolamento é parcial — e não é simulado.** As restrições implementadas são aplicadas pelo kernel do Windows: quando o container tenta usar um recurso bloqueado, quem nega é o sistema operacional. O que **não** está isolado, e o motivo técnico de cada caso, está em [Isolamento](#isolamento). Os conceitos estão em [docs/ETAPA-1.md](docs/ETAPA-1.md), [docs/ETAPA-2.md](docs/ETAPA-2.md) e [docs/ETAPA-3.md](docs/ETAPA-3.md), e o passeio pelo código em [docs/CODIGO.md](docs/CODIGO.md).

## Novidades da v3.0 — Etapa 3

> Entrega final. Esta versão fecha o plano da disciplina e acrescenta rede, compose e escala. Tudo abaixo é novo em relação à [v2.0 (Etapa 2)](docs/ETAPA-2.md); os conceitos de SO de cada item estão em [docs/ETAPA-3.md](docs/ETAPA-3.md).

### Destaques

- **Confinamento do sistema de arquivos.** O container roda com **integridade baixa** (como a aba de um navegador) e só escreve no próprio rootfs e nos volumes. No resto do disco, o kernel nega. `--privileged` desliga.
- **Volumes.** `-v C:\pasta:destino`, volumes nomeados (`-v dados:destino`) e só leitura (`:ro`). Os dados sobrevivem ao `rm`.
- **Rede: portas publicadas.** `-p 8080:80` publica a porta do container no host por um proxy TCP, como o `docker-proxy`. O programa escuta na porta que recebe em `%PORT%`.
- **Compose.** Um arquivo `minicompose.ini` descreve vários serviços; `compose up` constrói, cria e sobe tudo na ordem certa, e `compose down` derruba.
- **Escala.** `--replicas 3` e `scale worker=5` criam e removem cópias de um container.
- **Erros detectados pelo kernel.** Falha de processo, limite de memória e limite de processos chegam por uma **porta de conclusão de E/S** e aparecem no `events`, no log e como motivo no `ps`.
- **Restart automático.** `--restart on-failure:3 | always`, com espera crescente entre tentativas.
- **Imagens sem configuração.** `build` de uma pasta sem arquivo nenhum: a pasta vira a imagem e o `.bat` dela vira o comando.

### Novos comandos

| Comando | O que faz |
|---|---|
| `compose up / down / ps / logs / config` | Sobe, derruba, lista e mostra os logs de um projeto descrito em `minicompose.ini` |
| `scale GRUPO=N` | Ajusta o número de réplicas |
| `events [-f] [--erros]` | Histórico de eventos e erros de todos os containers |
| `port CONTAINER` | Portas publicadas de um container |
| `volume ls / create / rm / inspect` | Volumes nomeados |

### Novas opções

| Onde | Opção | Efeito |
|---|---|---|
| `run`/`create` | `-p [IP:]HOST:CONTAINER`, `-p CONTAINER` | Publica uma porta (fixa ou sorteada) |
| `run`/`create` | `-v ORIGEM:DESTINO[:ro]` | Monta uma pasta do host ou um volume nomeado |
| `run`/`create` | `--restart on-failure[:N] \| always` | Reinicia o container sozinho |
| `run`/`create` | `--replicas N` | Cria N cópias, `NOME-1` a `NOME-N` |
| `run`/`create` | `--privileged` | Roda sem o confinamento do sistema de arquivos |
| `build` | `--cmd`, `-e` | Comando e variáveis da imagem, sem MiniDockerfile |
| `logs` | `-t`, vários nomes, nome de grupo | Horário de cada linha; junta réplicas e containers numa saída só, intercalada |

### Mudanças por baixo

- **Criação suspensa (`CREATE_SUSPENDED`).** O processo nasce pausado, entra no Job Object e só então começa a rodar. A classe `Process` do .NET foi substituída por `CreateProcessAsUser` via P/Invoke.
- **Logs com horário.** Cada linha é gravada com o instante em que foi escrita, o que permite intercalar os logs de várias réplicas. Logs antigos, sem horário, continuam legíveis.
- **stdout e stderr num pipe só.** O log mantém a ordem real em que as linhas foram escritas.
- **`HOSTNAME`.** Todo container recebe o próprio nome nessa variável.
- **Mensagem clara fora do Windows.** Em vez de um erro de DLL, "Executar containers requer Windows".

### Correções

- **A corrida da Etapa 1 foi fechada.** Antes, o processo podia executar algumas instruções fora do Job Object entre o `Process.Start` e a atribuição.
- **`rm` e `commit` não seguem junctions.** Apagar um container com volume não apaga os dados do host, e um `commit` não leva os dados do volume para a imagem.
- **`build .` na pasta do usuário** é recusado, em vez de copiar a pasta do próprio MiniDocker para dentro de si.

### Mudanças que quebram compatibilidade

- Os exemplos `gato` e `cachorro` saíram. No lugar entraram três aplicações de uso real: [`backup`](exemplos/backup), [`processador`](exemplos/processador) e [`painel`](exemplos/painel), mais um [`minicompose.ini`](exemplos/minicompose.ini) que sobe tudo junto.
- Containers rodam confinados por padrão. Um script que gravava fora do próprio rootfs precisa de um volume ou de `--privileged`.

### Limitações conhecidas

- Containers só rodam no Windows; no Linux, a suíte roda tudo o que não executa processos (129 testes) e ignora os 20 que dependem do Windows.
- O confinamento bloqueia **escrita**, não leitura.
- O `:ro` é um rótulo na pasta: a mesma pasta não pode ser só leitura num container e gravável em outro ao mesmo tempo (o MiniDocker recusa com uma mensagem).
- Sem rede isolada: o container usa a pilha de rede do host. O `-p` publica portas, mas o programa ainda poderia abrir qualquer outra.
- Réplicas de um grupo não compartilham uma porta fixa (não há balanceador); use porta sorteada (`-p 80`).
- A entrega das mensagens da porta de conclusão não é garantida pela documentação: os eventos servem para diagnóstico.

## Índice

- [Novidades da v3.0](#novidades-da-v30--etapa-3)
- [Requisitos](#requisitos)
- [Início rápido](#início-rápido)
- [Comandos](#comandos)
- [Imagens](#imagens)
- [Volumes](#volumes)
- [Rede: portas publicadas](#rede-portas-publicadas)
- [Compose](#compose)
- [Escala](#escala)
- [Erros, eventos e restart](#erros-eventos-e-restart)
- [Como funciona](#como-funciona)
- [Isolamento](#isolamento)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Entregas](#entregas)

## Requisitos

- Windows 10 1803 ou mais novo — os containers rodam via `cmd.exe` dentro de um Job Object (o exemplo `backup` usa o `tar.exe` e o `painel` usa o Windows PowerShell, que já vêm no Windows)
- .NET 10 SDK

O código compila em qualquer sistema operacional, mas só executa containers no Windows. Fora do Windows, os testes que dependem do `cmd.exe` e da API do Windows são **ignorados** em vez de falharem; os de imagem, build sem `RUN`, volumes, criação e validação rodam em qualquer sistema.

## Início rápido

```bat
dotnet build
dotnet test

:: um alias para encurtar os exemplos
doskey minidocker=src\MiniDocker.Cli\bin\Debug\net10.0\MiniDocker.Cli.exe $*

:: container descartavel, anexado ao terminal
minidocker run --rm scratch echo ola
```

### Exemplos

Para apresentar, siga o roteiro em [exemplos/README.md](exemplos/README.md): cada passo traz o comando, a saída esperada e o que explicar. São três aplicações de uso real:

| Imagem | Tipo | O que faz | Equivale a |
|---|---|---|---|
| [`exemplos/backup`](exemplos/backup) | **Roda e termina** | Compacta uma pasta do host num `.zip` datado, guarda num volume e apaga os backups antigos | Um job agendado, uma migração de banco |
| [`exemplos/processador`](exemplos/processador) | **Fica rodando** | Vigia uma pasta de fila; para cada log que chega, gera um relatório (linhas, erros, avisos) | Um worker de fila |
| [`exemplos/painel`](exemplos/painel) | **Serviço web** | Site em `http://localhost:8080` com os relatórios do processador | Um servidor web, uma API |

O sistema inteiro — 3 processadores e o painel — sobe com um comando:

```bat
cd exemplos
minidocker compose up
copy processador\amostras\*.log fila\entrada\
start http://localhost:8080
minidocker compose logs -f
minidocker compose down
```

Ou cada um à mão:

```bat
:: backup: a pasta vira a imagem, sem nenhum arquivo de configuracao
minidocker build -t backup exemplos\backup
minidocker run --rm -v %USERPROFILE%\Documents:origem:ro -v backups:destino backup

:: processador: worker em segundo plano, com a fila numa pasta do host
mkdir C:\fila\entrada
minidocker build -t processador exemplos\processador
minidocker run -d --name worker -v C:\fila:fila processador
copy exemplos\processador\amostras\*.log C:\fila\entrada\
minidocker logs worker                :: "api-2026-11-14.log: 9 linhas, 2 erros, 2 avisos"
type C:\fila\saida\api-2026-11-14.relatorio.txt
```

Os três conceitos que a demonstração separa:

| | O que é | Analogia |
|---|---|---|
| **Imagem** | Molde só-leitura: arquivos + comando padrão | A receita do bolo |
| **Container** | Uma cópia da imagem com estado próprio (seus arquivos, seu log) | Um bolo feito da receita |
| **Processo** | O programa rodando dentro do container | O bolo no forno |
| **Volume** | Uma pasta de fora, montada dentro do container | A forma, que sobra depois que o bolo acaba |

E o que cada comando faz com eles:

| Comando | Processos | Arquivos do container | Volumes | Container |
|---|---|---|---|---|
| `stop` | Morrem | Ficam | Ficam | Continua existindo, `Parado` |
| `start` | Nascem de novo, do zero | Os mesmos de antes | Os mesmos | O mesmo |
| `rm` | — | Apagados | **Ficam** | Deixa de existir |
| `run` | Nascem | Cópia nova da imagem | Montados | Um container **novo** |

## Comandos

A sintaxe segue a do Docker. Containers e imagens podem ser referidos pelo nome ou por um prefixo do id.

### Containers

| Comando | O que faz |
|---|---|
| `run [opções] IMAGEM [COMANDO]` | Cria e executa. Sem `COMANDO`, usa o comando padrão da imagem |
| `create [opções] IMAGEM [COMANDO]` | Cria sem executar (estado `Criado`) |
| `start [-a] CONTAINER...` | Inicia em segundo plano; `-a` anexa ao terminal |
| `stop CONTAINER...` | Encerra o grupo inteiro (`TerminateJobObject`); código de saída 137 |
| `restart CONTAINER...` | `stop` + `start` |
| `rm [-f] CONTAINER...` | Remove registro, rootfs e log; `-f` para antes se estiver rodando. Volumes ficam |
| `ps [-a] [-q]` | Lista os em execução; `-a` inclui os parados |
| `logs [-f] [-t] NOME...` | Saída do container; `-f` acompanha, `-t` mostra o horário. Com vários nomes, ou o nome de um grupo, junta tudo intercalado por horário |
| `port CONTAINER` | Portas publicadas: `80/tcp -> 127.0.0.1:8080` |
| `exec CONTAINER COMANDO` | Executa mais um processo **dentro do mesmo Job Object** |
| `stats [CONTAINER...]` | CPU %, pico de memória e processos, lidos ao vivo do Job Object |
| `inspect NOME...` | Registro completo em JSON (container ou imagem) |
| `scale GRUPO=N` | Ajusta o número de réplicas de um grupo (ver [Escala](#escala)) |
| `events [-f] [--erros] [NOME...]` | Histórico de eventos e erros; `-f` acompanha ao vivo (ver [Erros](#erros-eventos-e-restart)) |

Opções de `run`/`create`:

| Opção | Efeito | Mecanismo |
|---|---|---|
| `-d` | Segundo plano; imprime o id | Processo shim (ver [Como funciona](#como-funciona)) |
| `-it` | Interativo, ligado ao terminal (sem log) | Processo herda o console |
| `--name NOME` | Nome do container; sem ele, um nome é gerado | |
| `-e CHAVE=valor` | Variável de ambiente; `-e CHAVE` copia a do host | Sobrescreve a da imagem |
| `-v PASTA:DESTINO` | Monta uma pasta do host no container | Junction NTFS (ponto de reanálise) |
| `-v VOLUME:DESTINO` | Monta um volume nomeado; criado se não existir | Idem, para `.minidocker\volumes\VOLUME` |
| `-v ...:ro` | Volume só leitura | A pasta é rotulada Medium, e o container (Low) não escreve nela |
| `-p 8080:80` | Publica a porta 80 do container em `127.0.0.1:8080` | Proxy TCP no processo dono; o programa escuta em `%PORT%` |
| `-p 80` | Idem, numa porta do host sorteada (veja com `port` ou `ps`) | |
| `-p 0.0.0.0:8080:80` | Publica para a rede, não só para esta máquina | |
| `--privileged` | Desliga o confinamento: o container escreve onde o usuário escreveria | Processo roda em integridade Medium |
| `--restart on-failure[:N]` | Reinicia se terminar com erro, até N vezes; `always` reinicia sempre | O dono sobe outro processo no mesmo Job Object |
| `--replicas N` | Cria N containers iguais, `NOME-1` a `NOME-N` (exige `-d` e `--name`) | Um Job Object por réplica |
| `-m 256m` | Limite de memória | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` / `JOB_MEMORY` |
| `--cpus 0.5` | Limite de CPU, em núcleos | `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` |
| `--pids-limit 10` | Máximo de processos vivos | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` |
| `--rm` | Remove o container ao terminar | |

### Imagens

| Comando | O que faz |
|---|---|
| `build -t NOME[:TAG] PASTA` | Transforma a pasta numa imagem (ver [Imagens](#imagens)) |
| `images [-q]` | Lista as imagens, com o comando padrão de cada uma |
| `rmi IMAGEM...` | Remove imagens |
| `commit CONTAINER NOME[:TAG]` | Congela o rootfs atual de um container numa imagem (sem os volumes) |
| `save -o ARQUIVO.zip IMAGEM` | Exporta para um zip, para levar a outra máquina |
| `load -i ARQUIVO.zip` | Importa |

A imagem `scratch` é embutida e vazia.

### Volumes

| Comando | O que faz |
|---|---|
| `volume ls` | Lista os volumes nomeados, o tamanho e quem os usa |
| `volume create NOME...` | Cria volumes (o `-v` também cria sozinho) |
| `volume rm NOME...` | Apaga o volume **e os dados**; recusa se algum container o usa |
| `volume inspect NOME...` | Detalhes em JSON |

## Imagens

Uma imagem é uma **pasta de arquivos** mais o **comando padrão** que roda quando alguém dá `run`. Criar um container **copia** a pasta da imagem: cada container altera a própria cópia sem afetar a imagem nem os vizinhos.

### Do jeito simples: a pasta vira a imagem

Não é preciso nenhum arquivo de configuração. Basta uma pasta com o programa:

```text
exemplos\backup\
    backup.bat
```

```bat
minidocker build -t backup exemplos\backup
```

```text
Sem MiniDockerfile: a pasta inteira vira a imagem.
Passo 1/3 : FROM scratch
Passo 2/3 : COPY . .
Passo 3/3 : CMD backup.bat
Imagem 8c1f0e2a9b3d construida como backup:latest
Comando padrao: backup.bat
```

O comando padrão é o único `.bat`, `.cmd` ou `.exe` da pasta. Se houver mais de um, ou para mudar alguma coisa, use as opções do `build`:

| Opção | Efeito |
|---|---|
| `--cmd "COMANDO"` | Comando padrão. Ex.: `--cmd "relatorio.bat --completo"` |
| `-e CHAVE=valor` | Variável que todo container da imagem recebe (repetível) |

```bat
minidocker build -t backup-diario --cmd backup.bat -e MANTER=7 exemplos\backup
```

### Com MiniDockerfile (opcional)

Para quem precisa rodar algo **durante** o build, ou partir de outra imagem, a pasta pode ter um `MiniDockerfile` — um subconjunto do Dockerfile. Quando ele existe, é ele que manda:

```dockerfile
FROM scratch                          # ou outra imagem local
COPY processador.bat .                # da pasta para dentro da imagem
ENV INTERVALO=2                       # variável herdada pelos containers
RUN echo %DATE% %TIME:~0,8%> versao.txt   # roda AGORA, num container de build isolado
CMD processador.bat                   # comando padrão do run
```

O `RUN` não roda no host: roda num **container temporário**, com Job Object, restrições, ambiente isolado e o mesmo confinamento de escrita, tendo a imagem em construção como diretório de trabalho. O que ele gravar ali vira parte da imagem. `COPY` não aceita caminhos que saiam da pasta (`..\`). As opções `--cmd` e `-e` do `build` valem por cima do que o MiniDockerfile definir.

O Docker evita a cópia usando **camadas** e um sistema de arquivos de união (overlayfs no Linux, wcifs no Windows): a imagem é só-leitura e cada container ganha uma camada gravável por cima. No MiniDocker as camadas ficam "achatadas" — cada `FROM` e cada container é uma cópia completa. O custo é disco e tempo de criação; o comportamento observável (o container não altera a imagem) é o mesmo.

## Volumes

Tudo o que um container grava no próprio rootfs some no `rm`. Um **volume** é uma pasta de fora montada dentro do container: o que ele grava ali fica.

```bat
:: pasta do host: o container ve C:\fila como "fila"
minidocker run -d -v C:\fila:fila processador

:: volume nomeado: o MiniDocker guarda em %USERPROFILE%\.minidocker\volumes\backups
minidocker run --rm -v %USERPROFILE%\Documents:origem:ro -v backups:destino backup
minidocker volume ls

:: so leitura: o container le a pasta, mas nao consegue alterar nada nela
minidocker run --rm -v %USERPROFILE%\Documents:origem:ro scratch "echo x> origem\teste.txt"
:: Acesso negado.
```

| | Pasta do host (`C:\fila:fila`) | Volume nomeado (`backups:destino`) |
|---|---|---|
| Onde ficam os dados | Onde você escolheu | `.minidocker\volumes\NOME` |
| Criado por | Você (ou o `-v`, se não existir) | O `-v` ou `volume create` |
| Apagado por | Ninguém | `volume rm`, e só se nenhum container o usa |
| Se o destino já existe na imagem | O conteúdo da imagem fica escondido | Um volume vazio recebe o conteúdo da imagem |

**Como funciona:** o volume é uma **junction NTFS** dentro do rootfs apontando para a pasta de fora — a versão Windows de um *bind mount*. O `rm` remove a junction **sem segui-la**, e o `commit` não copia o que está atrás dela. Detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md#3-volumes).

## Rede: portas publicadas

```bat
minidocker run -d --name painel -p 8080:80 -v C:\demo\fila:fila painel
minidocker port painel           :: 80/tcp -> 127.0.0.1:8080   (interna 51234)
curl http://localhost:8080/saude :: ok painel
```

O Windows não dá a um processo uma pilha de rede própria sem Server Silos, então todo container usa a rede do host. O que o MiniDocker faz é o que o `docker-proxy` faz:

```text
navegador ──► 127.0.0.1:8080 ──proxy do MiniDocker──► 127.0.0.1:51234 ──► programa do container
              porta publicada    (no processo dono)     porta interna, em %PORT% e %PORT_80%
```

1. A cada `start`, o MiniDocker sorteia uma **porta interna** livre e a entrega ao programa em `PORT` (e `PORT_80`, `PORT_443`... para cada porta publicada).
2. O processo dono do container abre a porta do host e repassa cada conexão para a porta interna — nos dois sentidos, ao mesmo tempo.
3. Quando o container para, o proxy fecha: a porta só existe enquanto o container roda.

Por padrão a porta é publicada só em `127.0.0.1` (sem aviso do firewall). Para expor na rede: `-p 0.0.0.0:8080:80`. Réplicas não podem dividir uma porta fixa; use `-p 80` para que cada uma ganhe uma porta sorteada. Detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md#5-rede-portas-publicadas).

## Compose

Um arquivo `minicompose.ini` descreve um sistema de vários containers. É um INI de propósito — uma seção por serviço, uma opção por linha, as mesmas opções do `run`:

```ini
projeto = relatorios

# tres workers dividindo a mesma fila
[processador]
build    = processador
replicas = 3
volume   = ./fila:fila
restart  = on-failure:3

# o site, que so sobe depois dos workers
[painel]
build   = painel
porta   = 8080:80
volume  = ./fila:fila
env     = TITULO=Logs analisados
depende = processador
```

| Opção | Equivale a | Exemplo |
|---|---|---|
| `build` | `build` antes de subir (pasta relativa ao arquivo) | `build = painel` |
| `imagem` | Imagem já existente (ou o nome da que o `build` cria) | `imagem = backup:latest` |
| `comando` | O `COMANDO` do `run` | `comando = backup.bat` |
| `replicas` | `--replicas` | `replicas = 3` |
| `porta` | `-p` (repetível) | `porta = 8080:80` |
| `volume` | `-v` (repetível; `./` é relativo ao arquivo) | `volume = ./fila:fila:ro` |
| `env` | `-e` (repetível) | `env = MODO=producao` |
| `memoria`, `cpus`, `pids` | `-m`, `--cpus`, `--pids-limit` | `memoria = 256m` |
| `restart` | `--restart` | `restart = always` |
| `privilegiado` | `--privileged` | `privilegiado = sim` |
| `depende` | Ordem de subida (separados por vírgula) | `depende = banco, fila` |

| Comando | O que faz |
|---|---|
| `compose up` | Constrói as imagens, cria os containers que faltam (ajustando as réplicas) e sobe tudo, na ordem das dependências |
| `compose down` | Para e remove os containers do projeto. Volumes e imagens ficam |
| `compose ps` | Containers do projeto |
| `compose logs [-f] [SERVICO...]` | Logs de todos os serviços numa saída só, com o nome de cada container colorido |
| `compose config` | Mostra o arquivo interpretado: caminhos resolvidos e ordem de subida |

O arquivo padrão é `minicompose.ini` na pasta atual; outro com `--file CAMINHO`. Erros apontam a linha: `Linha 12: [painel] opcao desconhecida 'cor'`. Cada serviço vira um grupo de réplicas (`painel-1`, `processador-1..3`), então `scale` e `logs GRUPO` funcionam nele também.

## Escala

Várias cópias do mesmo container, para dividir trabalho:

```bat
minidocker run -d --replicas 3 --name worker -v C:\fila:fila processador   :: worker-1, worker-2, worker-3
minidocker scale worker=5                                                   :: + worker-4, worker-5
minidocker scale worker=2                                                   :: - worker-5, -4, -3
```

Cada réplica é um container completo — Job Object, rootfs, log, limites — com a configuração da primeira (imagem, comando, ambiente, volumes, portas, limites, restart). Cada uma recebe o próprio nome em `%HOSTNAME%`. O `scale` remove primeiro as de número mais alto e sobe as que estiverem paradas.

```bat
minidocker logs -f worker        :: as 3 replicas numa saida so, intercaladas pelo horario
```

```text
worker-1 | [10:20:03] worker-1: lote-01.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
worker-2 | [10:20:03] worker-2: lote-02.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
worker-3 | [10:20:04] worker-3: lote-03.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
```

Réplicas que dividem uma pasta precisam de **exclusão mútua** para não processar o mesmo arquivo duas vezes. O exemplo `processador` usa o próprio sistema de arquivos para isso: cada réplica tenta **mover** o arquivo da fila para uma pasta só dela, e um `move` no mesmo disco é um rename **atômico** no NTFS — só uma consegue. Detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md#6-escala).

## Erros, eventos e restart

O Job Object de cada container é ligado a uma **porta de conclusão de E/S**, e o kernel posta nela o que acontece com o grupo. O dono do container lê essas mensagens numa thread e registra os erros sem precisar ficar consultando:

| O kernel avisa | Quando |
|---|---|
| Processo terminou com falha | Saiu por exceção (violação de acesso, estouro de pilha...) — código `0xC0000005` etc. |
| Limite de memória atingido | Uma alocação passou do `-m` |
| Limite de processos atingido | O kernel recusou criar um processo por causa do `--pids-limit` |

```bat
minidocker events                 :: tudo: criado, iniciado, reiniciando, falhou, parado, removido
minidocker events --erros -f      :: so os erros, ao vivo
minidocker ps -a                  :: Finalizado (1: limite de memoria atingido pelo processo 4321)
minidocker logs worker            :: [minidocker] kernel: limite de processos atingido...
```

Com `--restart on-failure:3`, um container que termina com erro é reiniciado **dentro do mesmo Job Object**, com espera crescente entre as tentativas (1 s, 2 s, 4 s... até 30 s). Um `stop` desliga o reinício. O `ps` mostra quantos reinícios houve e o último motivo. Detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md#4-erros-eventos-do-kernel).

## Como funciona

```text
minidocker run -d -v C:\fila:fila processador
    │
    ├─ create: copia o rootfs da imagem, monta os volumes, grava o registro (Criado)
    │
    └─ lança o shim em segundo plano e sai ─────────┐
                                                    ▼
                          minidocker __shim <nome>   (dono do container)
                              ├─ 1. rotula rootfs e volumes com integridade Low
                              │     e abre as portas publicadas (-p) ← proxy TCP
                              ├─ 2. cria o Job Object com nome    ← trava: um start por vez
                              ├─ 3. aplica restrições e limites   ← kernel passa a impor
                              │     e liga a porta de conclusão   ← kernel passa a avisar erros
                              ├─ 4. marca Executando
                              ├─ 5. CreateProcessAsUser(token Low, CREATE_SUSPENDED)
                              ├─ 6. atribui o processo ao job     ← ainda não executou nada
                              ├─ 7. ResumeThread                  ← só agora o cmd.exe roda
                              ├─ 8. grava stdout/stderr no log
                              ├─ 9. processo principal termina    ← encerra os netos
                              │     --restart? volta ao passo 5, no mesmo job
                              ├─ 10. lê a contabilidade do grupo
                              └─ 11. marca Finalizado (com o motivo) ou Parado; grava o evento
```

O **shim** cumpre o papel do `containerd-shim` do Docker: é ele, e não o CLI, que segura o Job Object, então o terminal pode ser fechado sem matar o container. No modo anexado (sem `-d`), o próprio CLI é o dono, e Ctrl+C equivale a `stop`.

O Job Object recebe um **nome** no namespace da sessão (`Local\MiniDocker-<id>`). É assim que outros processos encontram o container:

| Comando | O que faz com o nome |
|---|---|
| `stop` | `OpenJobObject` + `TerminateJobObject` — mata o grupo inteiro de uma vez |
| `exec` | `OpenJobObject` + `AssignProcessToJobObject` — o processo novo herda limites, restrições e o confinamento |
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
| `containers\<nome>\` | Rootfs de cada container (os volumes aparecem dentro dele como junctions) |
| `logs\<id>.log` | Saída de cada container; acumula entre restarts |
| `images.json` | Registro das imagens |
| `images\<id>\rootfs\` | Rootfs de cada imagem |
| `volumes\<nome>\` | Dados de cada volume nomeado |
| `eventos.jsonl` | Histórico de eventos e erros de todos os containers (`minidocker events`) |

Cada linha de `logs\<id>.log` começa com o horário em que foi gravada (`2026-11-15T10:00:00.1234567Z texto`); o `logs` o esconde, e o `logs -t` mostra.

O CLI e os shims gravam no mesmo JSON ao mesmo tempo. A exclusão mútua entre processos é feita com um arquivo de trava aberto com `FileShare.None`, e cada gravação lê o registro mais recente antes de alterar.

## Isolamento

### Imposto pelo kernel

| Restrição | Efeito |
|---|---|
| **Integridade baixa** (Low) | O container só **escreve** no próprio rootfs e nos volumes graváveis. No resto do disco, o kernel nega — mesmo que a ACL deixe o usuário escrever |
| **Criação suspensa** | O processo entra no Job Object antes de executar a primeira instrução: nada roda fora do grupo |
| `UILIMIT_GLOBALATOMS` | O grupo recebe uma **tabela de atoms global própria** — um namespace privado |
| `UILIMIT_HANDLES` | Não alcança handles de janela de processos de fora do grupo |
| `UILIMIT_READCLIPBOARD` / `WRITECLIPBOARD` | Não lê nem escreve na área de transferência do host |
| `UILIMIT_DESKTOP` | Não cria nem troca de desktop |
| `UILIMIT_SYSTEMPARAMETERS` / `DISPLAYSETTINGS` | Não altera configurações do sistema |
| `UILIMIT_EXITWINDOWS` | Não desliga nem reinicia o Windows |
| Limites de memória, CPU e processos | Ultrapassar faz a alocação ou a criação de processo falhar |
| Ambiente próprio | Não herda nenhuma variável do host; só entra o que a imagem ou o `-e` definem |
| `LIMIT_KILL_ON_JOB_CLOSE` | Nenhum processo sobrevive ao fim do container |

### Limites honestos

| Recurso | O que fica de fora |
|---|---|
| Leitura do disco | A integridade baixa bloqueia **escrita**, não leitura: o container ainda **lê** fora do rootfs. Bloquear leitura exigiria rotular o disco inteiro com *no-read-up*. |
| Imagem com userland própria | O container roda o `cmd.exe` do host. A imagem empacota os **arquivos da aplicação**, não um Windows inteiro. |
| Pasta do host montada como volume gravável | Recebe o rótulo Low e continua com ele depois do container: outros processos Low também passam a poder escrever nela. Use `:ro` quando o container só precisa ler (montar com `:ro` devolve a pasta a Medium). |
| Eventos do kernel | A documentação não garante a entrega das mensagens da porta de conclusão (a fila pode encher). Servem para diagnóstico; a contabilidade continua vindo do Job Object. |

### Fora de alcance

| Recurso | Por quê |
|---|---|
| Namespace de processos | O container enxerga o host via `tasklist`. Filtrar isso exige **Server Silos**, APIs não documentadas usadas pelo Host Compute System — seria embrulhar o runtime da Microsoft em vez de construir o próprio. |
| Rede isolada | O container usa a pilha de rede do host, pelo mesmo motivo. O `-p` publica portas por um proxy, mas não impede o programa de abrir outras. |

No Linux essa divisão é mais clara: **isolamento** vem de namespaces e **controle de recursos** vem de cgroups. O Job Object do Windows corresponde aos cgroups e cobre só uma parte dos namespaces — a do subsistema de janelas. O nível de integridade faz o papel de um controle de acesso obrigatório (como o SELinux/AppArmor).

### Como verificar

```bat
:: o container nao escreve fora do proprio rootfs...
minidocker run --rm scratch "echo invasao> %USERPROFILE%\invasao.txt"
:: Acesso negado.
:: ...a nao ser que seja privilegiado
minidocker run --rm --privileged scratch "echo invasao> %USERPROFILE%\invasao.txt & del %USERPROFILE%\invasao.txt"

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

:: o que ainda NAO esta isolado, honestamente: leitura, processos, rede
minidocker run --rm scratch "type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

> Nos exemplos acima, `%USERPROFILE%` é expandido pelo `cmd` **do host** antes de chegar ao container, que não tem essa variável.

## Estrutura do projeto

```text
src/MiniDocker.Nucleo/
  Modelos/        Conteiner, Imagem, Montagem (volume), EstadoConteiner, LimitesRecursos, OpcoesConteiner
  Interop/        P/Invoke: Job Objects, CreateProcess suspenso, integridade, porta de conclusão
  Rede/           Portas publicadas: proxy TCP
  Compose/        Leitor do minicompose.ini e compose up/down
  Repositorios/   Persistência em JSON com trava entre processos
  Servicos/       Ciclo de vida, restart, escala, imagens, build, volumes, eventos, execução
src/MiniDocker.Cli/      Interface de linha de comando e shim
tests/MiniDocker.Testes/ Testes unitários
exemplos/                backup, processador, painel, minicompose.ini e roteiro de demonstração
docs/ETAPA-1.md          Conceitos: Job Objects, isolamento, contabilidade
docs/ETAPA-2.md          Conceitos: segundo plano, job com nome, imagens
docs/ETAPA-3.md          Conceitos: confinamento, criação suspensa, volumes, eventos, rede, escala, compose
docs/CODIGO.md           Passeio pelo código, arquivo por arquivo
```

## Entregas

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª — isolamento e ciclo de vida** | Implementada | O container vira um grupo de processos num Job Object, com restrições de interface impostas pelo kernel, tabela de atoms própria, ambiente isolado do host e encerramento sem deixar órfãos. |
| **2ª — segundo plano e imagens** | Implementada | Shim dono do container; Job Object com nome; `start`, `stop` real via `TerminateJobObject`, `restart`, `exec`, `logs`, `stats`; limites de memória, CPU e processos; `build`, `images`, `rmi`, `commit`, `save`/`load`. |
| **3ª — v3.0** | Implementada | Ver [Novidades da v3.0](#novidades-da-v30--etapa-3): confinamento, criação suspensa, volumes, portas publicadas, compose, réplicas e `scale`, erros pelo kernel, `--restart`, logs intercalados, `build` sem configuração. Conceitos em [docs/ETAPA-3.md](docs/ETAPA-3.md). |
