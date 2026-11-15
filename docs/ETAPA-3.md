# Etapa 3 (v3.0) — Confinamento, volumes, erros, rede, escala e compose

Conceitos de Sistemas Operacionais da entrega final. As anteriores estão em [ETAPA-1.md](ETAPA-1.md) (Job Objects, isolamento, contabilidade) e [ETAPA-2.md](ETAPA-2.md) (segundo plano, job com nome, imagens).

| Item | O que traz | Conceito de SO |
|---|---|---|
| [Confinamento do sistema de arquivos](#1-confinamento-do-sistema-de-arquivos) | O container não escreve fora da própria pasta | Controle de acesso obrigatório (MAC) |
| [Criação suspensa](#2-criação-suspensa) | Fecha a corrida da Etapa 1 | Contador de suspensão de threads |
| [Volumes](#3-volumes) | Dados que sobrevivem ao `rm` | Pontos de reanálise do NTFS (*bind mount*) |
| [Erros: eventos do kernel](#4-erros-eventos-do-kernel) | Saber por que um container falhou; `events`; `--restart` | Portas de conclusão; supervisão de processos |
| [Rede: portas publicadas](#5-rede-portas-publicadas) | `-p 8080:80` | Sockets, portas efêmeras, proxy de transporte |
| [Escala](#6-escala) | Várias réplicas dividindo trabalho | Exclusão mútua por operação atômica |
| [Compose e logs juntos](#7-compose-e-logs-juntos) | Um sistema inteiro com um comando | Ordenação topológica; relógio comum |
| [Imagens sem configuração](#8-imagens-sem-configuração) | `build` de uma pasta, sem arquivo nenhum | — |

## 1. Confinamento do sistema de arquivos

### O problema

Até a Etapa 2, o rootfs era só o diretório de trabalho. O container rodava com a identidade de quem o lançou e escrevia onde essa pessoa escreveria: `echo x > %USERPROFILE%\arquivo.txt` funcionava.

### Dois controles de acesso sobrepostos

O Windows decide se um processo pode abrir um arquivo em duas etapas:

| | Pergunta | Quem define | Tipo |
|---|---|---|---|
| **ACL** (DACL) | *Este usuário* pode escrever? | O dono do arquivo | Discricionário (DAC) |
| **Rótulo de integridade** | *Este processo* pode escrever? | O sistema | Obrigatório (MAC) |

Todo processo e todo objeto têm um **nível de integridade**: `Low`, `Medium`, `High` ou `System`. Um programa comum roda em `Medium`; um objeto sem rótulo é tratado como `Medium` — ou seja, praticamente o disco inteiro. A regra padrão é **no-write-up**: um processo não escreve em objetos de nível maior que o dele, **mesmo que a ACL deixe**.

O MiniDocker usa isso assim:

```text
 processo do container ── Low ──┐
                                ├── escreve no rootfs     (rotulado Low)    ✔
                                ├── escreve no volume     (rotulado Low)    ✔
                                ├── escreve em C:\Users\… (Medium, padrão)  ✘ Acesso negado
                                └── lê C:\Windows\…       (leitura liberada) ✔
```

É o mecanismo do **modo protegido dos navegadores** (o Internet Explorer e o Chrome rodam as abas em `Low` pelo mesmo motivo). A ideia é a mesma do SELinux/AppArmor no Linux: uma política do sistema que o dono do arquivo não consegue afrouxar.

### Como foi feito

[`Interop/IntegridadeBaixa.cs`](../src/MiniDocker.Nucleo/Interop/IntegridadeBaixa.cs):

1. **Token Low** — `OpenProcessToken` no próprio processo, `DuplicateTokenEx` para ter uma cópia primária, e `SetTokenInformation(TokenIntegrityLevel)` com o SID `S-1-16-4096` (Low). O usuário continua o mesmo; muda só o rótulo.
2. **Pasta Low** — `SetNamedSecurityInfo` grava no rootfs um rótulo obrigatório `S:(ML;OICI;NW;;;LW)`: *mandatory label*, herdado por arquivos e subpastas (`OICI`), política *no-write-up* (`NW`), nível Low (`LW`). O Windows propaga o rótulo para o que já existe na pasta. É o mesmo que `icacls PASTA /setintegritylevel (OI)(CI)low`.
3. **Processo com o token** — `CreateProcessAsUser` em vez de `CreateProcess`. Um usuário comum pode fazer isso sem privilégio especial porque o token é uma cópia do próprio, só que mais restrita.

O rotulamento acontece a cada `start` (o rootfs e cada volume gravável). O `exec` e o `RUN` do build também rodam em `Low`.

`--privileged` desliga tudo isso: o processo roda em `Medium`, como antes. É útil para comparar na demonstração.

### Limite honesto

A política padrão bloqueia **escrita**, não leitura: o container ainda **lê** fora do rootfs (`type C:\Windows\...\hosts` funciona). Bloquear leitura exigiria rotular os objetos com *no-read-up*, o que não é viável para o disco inteiro.

## 2. Criação suspensa

### A corrida da Etapa 1

Com a classe `Process` do .NET, o processo começava a rodar **antes** de entrar no Job Object ([ETAPA-1.md, seção 5](ETAPA-1.md#5-uma-corrida-que-não-deu-para-fechar)). Na janela entre `Process.Start` e `AssignProcessToJobObject`, ele estava fora do grupo: sem limites e, se criasse um filho nesse instante, o filho escaparia.

### A solução

Toda thread tem um **contador de suspensão**. Enquanto ele for maior que zero, o escalonador não a executa. `CREATE_SUSPENDED` faz a thread principal nascer com o contador em 1:

```text
 CreateProcessAsUser(..., CREATE_SUSPENDED)   processo existe, nenhuma instrução executada
 AssignProcessToJobObject                     entra no grupo
 ResumeThread                                 contador vai a 0: só agora o cmd.exe roda
```

O processo já nasce rotulado `Low` e só executa dentro do job. É o que o Docker faz.

### O preço

A classe `Process` esconde muita coisa que passou a ser feita à mão em [`Interop/ProcessoNativo.cs`](../src/MiniDocker.Nucleo/Interop/ProcessoNativo.cs) e [`Servicos/ExecutorProcesso.cs`](../src/MiniDocker.Nucleo/Servicos/ExecutorProcesso.cs):

| O que a classe `Process` fazia | Como ficou |
|---|---|
| Montar o ambiente | Bloco `CHAVE=valor\0...\0\0` em UTF-16, ordenado (`CREATE_UNICODE_ENVIRONMENT`) |
| Redirecionar a saída | `CreatePipe`; só a ponta de escrita é **herdável** e vai para o filho em `STARTUPINFO` |
| Ler a saída | Uma thread lê o pipe até o fim de arquivo, que só chega quando **todas** as pontas de escrita fecharem |
| Não vazar handles | Uma trava serializa "criar pipe → criar processo → fechar a ponta do pai", senão outro processo criado no meio herdaria o pipe |

Agora stdout e stderr vão para o **mesmo** pipe, então o log mantém a ordem real em que as linhas foram escritas.

## 3. Volumes

### O que é

```bat
minidocker run -v C:\fila:fila processador          :: pasta do host
minidocker run -v backups:destino backup            :: volume nomeado
minidocker run -v C:\docs:origem:ro backup          :: só leitura
```

Uma pasta de fora aparece dentro do rootfs. É assim que um container guarda dados que sobrevivem ao `rm` — e que o host entrega arquivos para ele processar.

### Como foi feito

Uma **junction NTFS** dentro do rootfs apontando para a pasta de fora (o mesmo que `mklink /J`). Uma junction é um **ponto de reanálise** (*reparse point*): um diretório com uma marca que o NTFS lê ao abrir o caminho e que diz "continue a partir de outro lugar". É a versão Windows de um *bind mount*. Diferente de um link simbólico, não exige privilégio de administrador.

### Não seguir o link

Um ponto de reanálise é transparente para quase tudo — inclusive para quem não deveria segui-lo:

| Operação | Se seguisse a junction | O que o MiniDocker faz |
|---|---|---|
| `rm` | Apagaria os **dados do host** | Remove a junction como link ([`Workspace.ApagarDiretorio`](../src/MiniDocker.Nucleo/Workspace.cs)) |
| `commit` | Copiaria os dados do volume para a imagem | Pula pontos de reanálise na cópia |
| `images`/tamanho | Contaria o volume como parte do container | Idem |

### `:ro` de graça

Um volume só leitura é rotulado `Medium`, como o resto do disco, em vez de `Low`. O container lê (leitura é liberada) mas não escreve (no-write-up). O rótulo é gravado explicitamente a cada `start`, e não só omitido, porque a pasta pode ter sido montada gravável (e virado `Low`) por outro container antes. Quem impõe o `:ro` é o mesmo mecanismo da seção 1 — por isso `:ro` com `--privileged` é recusado: sem confinamento, não haveria quem o impusesse.

### Volume nomeado

Fica em `.minidocker\volumes\NOME`, criado pelo primeiro `-v` que o usar. Como no Docker, um volume **vazio** montado sobre uma pasta que existe na imagem recebe o conteúdo dela; uma pasta do host esconde o conteúdo da imagem. `volume rm` recusa enquanto algum container o tiver montado.

## 4. Erros: eventos do kernel

### O problema

Até a Etapa 2, quando um container terminava, sobrava só um número: `Finalizado (1)`. Foi o programa que falhou? Estourou a memória? O kernel recusou criar um processo por causa do `--pids-limit`? Pior: há erros que não derrubam o container — o kernel recusa um processo, o programa segue com um resultado errado, e ninguém fica sabendo.

### Porta de conclusão: o kernel avisa

Uma **porta de conclusão de E/S** é uma fila de mensagens do kernel. Associada a um Job Object (`SetInformationJobObject(JobObjectAssociateCompletionPortInformation)`), ela passa a receber as mensagens do grupo. Uma thread do dono do container dorme em `GetQueuedCompletionStatus` e acorda quando o kernel posta algo:

| Mensagem (`JOB_OBJECT_MSG_*`) | Significado | O MiniDocker registra |
|---|---|---|
| `ABNORMAL_EXIT_PROCESS` | Um processo terminou por exceção (`0xC0000005`, `0xC00000FD`...) | "processo 4321 terminou com falha: violação de acesso" |
| `PROCESS_MEMORY_LIMIT` / `JOB_MEMORY_LIMIT` | Uma alocação passou do `-m` | "limite de memória atingido" |
| `ACTIVE_PROCESS_LIMIT` | Uma criação de processo foi recusada pelo `--pids-limit` | "limite de processos atingido" |
| `NEW_PROCESS` / `EXIT_PROCESS` | Funcionamento normal | Ignorados (seriam ruído) |

É notificação **dirigida pelo kernel** em vez de *polling*: ninguém fica perguntando ao container se está tudo bem. Código em [`Interop/MonitorEventosKernel.cs`](../src/MiniDocker.Nucleo/Interop/MonitorEventosKernel.cs).

O que é feito com cada erro:

- vai para o log do container (`[minidocker] kernel: ...`);
- vai para `eventos.jsonl`, que o `minidocker events` mostra (`--erros` filtra, `-f` acompanha);
- se o container terminar, vira o **motivo** no `ps`: `Finalizado (1: limite de memória atingido pelo processo 4321)`. O erro do kernel tem prioridade sobre o código de saída, porque explica mais: o "1" é só o que o programa devolveu depois de a alocação falhar.

O `eventos.jsonl` é gravado por vários processos (cada shim, o CLI). Cada gravação acontece dentro da mesma seção crítica entre processos do `containers.json` — o arquivo de trava aberto com `FileShare.None` ([ETAPA-2.md, seção 5](ETAPA-2.md#5-exclusão-mútua-entre-processos)). Sem ela, duas gravações simultâneas no fim do arquivo se sobrescreveriam; um teste com 200 gravações em paralelo pegou exatamente isso.

### Política de restart

```bat
minidocker run -d --restart on-failure:3 processador     :: até 3 reinícios, só se terminar com erro
minidocker run -d --restart always processador           :: sempre, até um stop
```

O dono do container, ao ver o processo principal terminar, consulta a política e o código de saída e, se for o caso, sobe outro processo **dentro do mesmo Job Object** — mesmos limites, mesma contabilidade acumulada, mesmo log. Entre as tentativas espera 1 s, 2 s, 4 s... até 30 s (*backoff* exponencial), para que um container que falha ao subir não vire um laço consumindo CPU. Um `stop` durante a espera é percebido e encerra o ciclo.

É o papel de um **supervisor de processos**: o `init`/`systemd` no Linux, as ações de recuperação de um serviço no Windows.

### Limite honesto

A documentação avisa que a entrega das mensagens da porta não é garantida (a fila pode encher). Os eventos servem para diagnóstico; a contabilidade continua vindo de `QueryInformationJobObject`. O código de saída de um processo que falhou é lido depois do evento, com o processo possivelmente já liberado — é melhor esforço.

## 5. Rede: portas publicadas

### O problema

No Linux, cada container do Docker ganha um **namespace de rede**: interfaces, tabela de rotas e portas próprias. Dois containers podem escutar na porta 80 ao mesmo tempo, e `-p 8080:80` é uma regra de NAT do kernel (iptables) que leva a porta 8080 do host até a 80 de um deles.

No Windows, isolar a pilha de rede de um processo exige Server Silos (o mesmo motivo do namespace de processos, fora de alcance). Todo container do MiniDocker usa a rede do host: se dois programas tentarem escutar na 80, o segundo falha.

### Como foi feito

O "porta 80 do container" vira um **nome**, e o MiniDocker faz a tradução com um proxy — exatamente o que o `docker-proxy` faz no Linux quando o NAT não alcança:

```text
navegador ──► 127.0.0.1:8080 ──► proxy (no shim) ──► 127.0.0.1:51234 ──► programa do container
              porta publicada                         porta interna = %PORT% = %PORT_80%
```

1. **Porta interna.** A cada `start`, o MiniDocker pede ao kernel uma porta livre (`bind` na porta 0) e a entrega ao programa nas variáveis `PORT` e `PORT_80`. Assim dez réplicas podem ter, todas, uma "porta 80", cada uma numa porta interna diferente.
2. **Proxy.** O processo dono do container abre a porta publicada e, para cada conexão aceita, abre outra até a porta interna e copia os bytes nos dois sentidos ao mesmo tempo. Quando um lado fecha a escrita, o proxy fecha a escrita do outro (*half-close*), e a conexão termina quando os dois sentidos acabarem. Código em [`Rede/ProxyTcp.cs`](../src/MiniDocker.Nucleo/Rede/ProxyTcp.cs).
3. **Tempo de vida.** O proxy vive no processo dono, então a porta publicada existe exatamente enquanto o container roda. Se a porta do host estiver ocupada, o `start` falha antes de mudar o estado.

Por padrão a porta é publicada só em `127.0.0.1` (o Docker usa `0.0.0.0`): não aparece o aviso do firewall na demonstração. `-p 0.0.0.0:8080:80` publica na rede.

### Limite honesto

O proxy publica portas, mas **não isola** a rede: o programa do container ainda pode abrir qualquer porta do host, conectar em qualquer lugar e ver as conexões das outras aplicações. Réplicas com porta fixa entrariam em conflito, então o MiniDocker exige porta sorteada (`-p 80`) para grupos; um balanceador que distribuísse uma porta única entre as réplicas seria o próximo passo.

## 6. Escala

```bat
minidocker run -d --replicas 3 --name worker -v C:\fila:fila processador
minidocker scale worker=5
minidocker scale worker=1
```

Cada réplica (`worker-1`, `worker-2`...) é um container completo, com Job Object, rootfs e log próprios, e a configuração da primeira. O `scale` cria as que faltam e para e remove as excedentes (as de número mais alto). Cada réplica recebe o próprio nome em `HOSTNAME`, como no Docker.

### Exclusão mútua entre containers

Réplicas que leem a mesma pasta de fila precisam de exclusão mútua — senão duas processam o mesmo arquivo. Não há memória compartilhada nem semáforo entre elas, mas há o sistema de arquivos. O exemplo `processador` "reivindica" cada arquivo assim:

```bat
move "fila\entrada\x.log" "fila\trabalhando\%HOSTNAME%\" || pula
```

Dentro do mesmo volume, `move` é um **rename**, e o NTFS faz rename de forma **atômica**: o arquivo está na entrada ou na pasta de uma réplica, nunca nas duas. Se duas réplicas tentam ao mesmo tempo, uma consegue e a outra recebe "arquivo não encontrado" e segue para o próximo. É a mesma ideia de um *test-and-set*: a operação que testa ("ainda está aqui?") e a que altera ("agora é meu") são uma só, então não há janela entre elas. Filas reais baseadas em diretório (o `Maildir` dos servidores de e-mail, por exemplo) funcionam assim.

## 7. Compose e logs juntos

### O arquivo

```ini
projeto = relatorios

[processador]
build    = processador
replicas = 3
volume   = ./fila:fila

[painel]
build   = painel
porta   = 8080:80
volume  = ./fila:fila
depende = processador
```

Um INI, e não YAML, de propósito: uma seção por serviço, uma opção por linha, as mesmas opções do `run`. Não há indentação que importe nem listas aninhadas, e o leitor é escrito à mão no projeto ([`Compose/ArquivoCompose.cs`](../src/MiniDocker.Nucleo/Compose/ArquivoCompose.cs)) — sem biblioteca externa. Todo erro aponta a linha.

### Ordem de subida: ordenação topológica

`depende` forma um grafo. O `compose up` sobe cada serviço depois dos que ele depende, numa **busca em profundidade** que marca os nós "em visita": encontrar de novo um nó em visita significa um ciclo (`a` depende de `b`, `b` depende de `a`), que é recusado. É o mesmo algoritmo de um `make` ou de um gerenciador de pacotes — e o mesmo problema do grafo de espera usado para detectar **deadlock**.

Cada serviço vira um grupo de réplicas marcado com o projeto, então `compose up` de novo só ajusta o que mudou no número de réplicas, e `compose down` remove só os containers do projeto (volumes e imagens ficam).

### Logs intercalados: um relógio comum

Com três réplicas escrevendo ao mesmo tempo, cada uma tem o seu arquivo de log. Para mostrá-los juntos na ordem em que as coisas aconteceram, cada linha passou a ser gravada com o **horário** (UTC, precisão de 100 ns). O `logs worker` lê os arquivos das réplicas e intercala as linhas pelo horário; o `-f` continua lendo de onde parou em cada arquivo.

A ordem é a do relógio da máquina, que é um só para todos os containers — numa única máquina isso basta. Num sistema distribuído, com relógios diferentes, seria preciso um relógio lógico (Lamport), assunto clássico de SO distribuídos.

## 8. Imagens sem configuração

Na Etapa 2, toda imagem precisava de um `MiniDockerfile`. Agora a pasta basta:

```bat
minidocker build -t backup exemplos\backup
```

Sem `MiniDockerfile`, o build equivale a `FROM scratch` + `COPY . .` + `CMD <o único .bat/.cmd/.exe da pasta>` — e mostra esses passos na saída, então o modelo continua visível. `--cmd` e `-e` no `build` definem ou trocam o comando e as variáveis sem arquivo nenhum. O `MiniDockerfile` continua existindo para o que só ele faz: `RUN` durante o build e `FROM` de outra imagem.

## 9. Perguntas prováveis

**"O container roda como outro usuário?"**
Não. O usuário é o mesmo; muda o **nível de integridade** do token. As ACLs continuam dizendo que o usuário pode escrever — e o rótulo diz que *este processo* não pode. Por isso não é preciso ser administrador nem criar contas.

**"Por que não `CreateRestrictedToken`?"**
Um token restrito tira grupos e privilégios, mas a escrita continua liberada onde a ACL do usuário permite — teria de mexer nas ACLs do disco. O nível de integridade inverte a lógica: bloqueia tudo por padrão e libera só o que foi rotulado.

**"Dá para o container se promover de volta a Medium?"**
Não. Um processo não aumenta o próprio nível de integridade, e um processo `Low` não consegue abrir processos `Medium` para escrever neles (a regra *no-write-up* vale para processos também).

**"E se o container apagar arquivos do volume?"**
Pode, se o volume for gravável — é para isso que ele existe. Para entregar dados que o container só deve ler, use `:ro`.

**"Por que junction e não link simbólico?"**
Criar link simbólico exige o privilégio `SeCreateSymbolicLinkPrivilege` (administrador ou modo desenvolvedor). Junction não exige nada, e serve para pastas locais, que é o caso.

**"Ainda há alguma janela antes de o processo entrar no job?"**
Não. Ele é criado suspenso e só é liberado depois de `AssignProcessToJobObject`. Se a atribuição falhar, ele é encerrado sem ter executado nada.

**"Por que uma porta de conclusão e não consultar o job de tempos em tempos?"**
Consultar (`QueryInformationJobObject` num laço) gasta CPU o tempo todo e ainda perde o que acontece entre duas consultas — um processo recusado pelo limite não deixa rastro na contabilidade. Com a porta, a thread fica bloqueada sem gastar nada e o kernel a acorda exatamente quando algo acontece.

**"O restart cria um container novo?"**
Não. É o mesmo container, o mesmo rootfs e o mesmo Job Object: só o processo é novo. Por isso os limites valem para a soma das tentativas e o log mostra todas em sequência.

**"Dois containers podem usar a porta 80?"**
Do ponto de vista deles, sim: cada um recebe uma porta interna diferente em `%PORT%` e é publicado numa porta diferente do host. O que não existe é uma pilha de rede separada para cada um.

**"O proxy deixa a conexão mais lenta?"**
Um pouco: cada byte passa por dois sockets a mais no mesmo processo. É o custo que o `docker-proxy` também tem; o NAT do kernel do Linux evita esse custo, mas não existe no Windows sem Server Silos.

**"E se duas réplicas moverem o mesmo arquivo exatamente ao mesmo tempo?"**
O NTFS serializa as duas operações de rename sobre o mesmo diretório. Uma termina primeiro; para a segunda, o arquivo de origem já não existe e ela falha. Nenhum arquivo é processado duas vezes, e nenhum se perde.
