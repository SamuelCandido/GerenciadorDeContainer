# Roteiro de demonstração

Passo a passo para mostrar o MiniDocker funcionando, usando as duas imagens de exemplo desta pasta:

| Imagem | Tipo de container | O que faz |
|---|---|---|
| [`gato`](gato) | **Roda e morre** | Mia uma vez e termina. Como um script ou uma migração de banco |
| [`cachorro`](cachorro) | **Roda em loop** | Late a cada 2 segundos até alguém dar `stop`. Como um servidor |

As saídas abaixo são ilustrativas: ids, horários e nomes gerados mudam a cada execução.

## Preparação

Na raiz do repositório, num `cmd`:

```bat
dotnet build
doskey minidocker=src\MiniDocker.Cli\bin\Debug\net10.0\MiniDocker.Cli.exe $*
```

O `doskey` cria um apelido, então daqui em diante basta digitar `minidocker`.

## 1. Build: da receita à imagem

Uma imagem é uma **pasta de arquivos + configurações** (comando padrão e variáveis). Ela é montada a partir de um `MiniDockerfile`, que segue a sintaxe do Dockerfile:

```dockerfile
FROM scratch                 # começa de uma pasta vazia
COPY gato.bat .              # copia o script para dentro da imagem
ENV NOME=Mingau              # variável que todo container vai ter
RUN echo %DATE% %TIME:~0,8% > construida_em.txt   # roda AGORA, no build
CMD gato.bat                 # o que roda quando alguém der "run"
```

```bat
minidocker build -t gato exemplos\gato
minidocker build -t cachorro exemplos\cachorro
minidocker images
```

```text
Passo 1/5 : FROM scratch
Passo 2/5 : COPY gato.bat .
Passo 3/5 : ENV NOME=Mingau
Passo 4/5 : RUN echo %DATE% %TIME:~0,8% > construida_em.txt
Passo 5/5 : CMD gato.bat
Imagem 3f9a1c2b7d10 construida como gato:latest
...
REPOSITORIO   TAG      IMAGE ID       CRIADA   TAMANHO
cachorro      latest   6b8fdde4ea60   ha 2 s   555 B
gato          latest   3f9a1c2b7d10   ha 3 s   412 B
```

**O que explicar:** o `RUN` não roda no computador do host — roda num container temporário, isolado. O arquivo que ele cria (`construida_em.txt`) passa a fazer parte da imagem, e todo container criado dela já nasce com ele.

## 2. Container que roda e morre: o gato

```bat
minidocker run --name mingau gato
minidocker ps -a
```

```text
    /\_/\
   ( o.o )
    > ^ <

Mingau: miau! Agora sao 14:32:07.
(esta imagem foi construida em 23/10/2026 14:31:50)
Mingau foi dormir. Fim do container.

CONTAINER ID   IMAGEM        COMANDO        CRIADO   STATUS                 NOME
a1b2c3d4e5f6   gato:latest   "gato.bat"     ha 2 s   Finalizado (0) ha 1 s  mingau
```

**O que explicar:** o processo acabou sozinho, com código 0 (sucesso). O container continua registrado em `Finalizado` — dá para ver os logs ou rodá-lo de novo com `start`.

## 3. Container que roda em loop: o cachorro

```bat
minidocker run -d --name rex cachorro
minidocker ps
minidocker logs rex
```

```text
7e8f9a0b1c2d

CONTAINER ID   IMAGEM            COMANDO          CRIADO   STATUS             NOME
7e8f9a0b1c2d   cachorro:latest   "cachorro.bat"   ha 5 s   Executando ha 5 s  rex

    / \__
   (    @\___
   /         O
  /   (_____/
 /_____/   U

Rex acordou! Este container ja foi iniciado 1 vez(es).
[14:33:01] Rex: au au! (latido 1)
[14:33:03] Rex: au au! (latido 2)
[14:33:05] Rex: au au! (latido 3)
```

**O que explicar:** o `-d` roda em segundo plano — o terminal fica livre e o cachorro continua latindo. Quem mantém o container vivo é um processo auxiliar do MiniDocker (o papel do `containerd-shim` no Docker), não o terminal. `logs -f rex` acompanha ao vivo (Ctrl+C para sair).

## 4. Stop: os processos morrem, o container fica

```bat
minidocker stop rex
minidocker ps -a
```

```text
rex

CONTAINER ID   IMAGEM            COMANDO          CRIADO    STATUS                NOME
7e8f9a0b1c2d   cachorro:latest   "cachorro.bat"   ha 40 s   Parado (137) ha 1 s   rex
a1b2c3d4e5f6   gato:latest       "gato.bat"       ha 1 min  Finalizado (0) ha 1 min  mingau
```

**O que explicar:** o `stop` chama `TerminateJobObject`, que mata **todos** os processos do grupo de uma vez — o `cmd.exe` e o `ping` que ele criou. O código 137 é o mesmo que o Docker mostra para um container morto à força. Mas o container `rex` **continua existindo**, com os seus arquivos.

## 5. Start: o mesmo container volta

```bat
minidocker start rex
minidocker logs rex
```

```text
rex

Rex acordou! Este container ja foi iniciado 1 vez(es).
[14:33:01] Rex: au au! (latido 1)
...
Rex acordou! Este container ja foi iniciado 2 vez(es).
[14:34:10] Rex: au au! (latido 1)
```

**O que explicar:** não foi preciso um novo `run`. O `start` voltou **o mesmo** container: o cachorro lembra que já foi iniciado antes, porque guarda a contagem num arquivo dentro do próprio container, e esse arquivo sobreviveu ao `stop`. O log também é o mesmo, por isso aparecem as duas execuções. Já os latidos recomeçam do 1: o processo é novo, a memória dele começa do zero.

## 6. Outro container da mesma imagem

```bat
minidocker run -d --name bidu -e NOME=Bidu cachorro
minidocker logs bidu
minidocker ps
```

```text
Bidu acordou! Este container ja foi iniciado 1 vez(es).
[14:35:20] Bidu: au au! (latido 1)

CONTAINER ID   IMAGEM            COMANDO          CRIADO    STATUS              NOME
9d8c7b6a5f4e   cachorro:latest   "cachorro.bat"   ha 4 s    Executando ha 4 s   bidu
7e8f9a0b1c2d   cachorro:latest   "cachorro.bat"   ha 2 min  Executando ha 1 min rex
```

**O que explicar:** cada `run` cria uma **cópia nova** da imagem. O `bidu` começa do zero ("1 vez") mesmo com o `rex` já tendo sido iniciado duas vezes: um container não enxerga os arquivos do outro. O `-e NOME=Bidu` sobrescreveu a variável que vinha da imagem.

## 7. Stats e exec: olhando dentro

```bat
minidocker stats
minidocker exec rex dir
```

```text
CONTAINER ID   NOME   CPU %   MEM PICO / LIMITE   PROCS / LIMITE   PROCS CRIADOS
9d8c7b6a5f4e   bidu   0.3%    4.1 MB / -          2 / -            5
7e8f9a0b1c2d   rex    0.2%    4.2 MB / -          2 / -            21

 Pasta de C:\Users\...\.minidocker\containers\rex
cachorro.bat
vezes.txt
```

**O que explicar:** o `stats` lê a contabilidade direto do Job Object do Windows — é o kernel quem soma o consumo de todos os processos do container. O `exec` roda mais um comando **dentro** do container: entra no mesmo grupo e enxerga a pasta dele (com o `vezes.txt` que o cachorro gravou).

## 8. Rm: agora sim deixa de existir

```bat
minidocker rm -f mingau rex bidu
minidocker ps -a
minidocker images
```

```text
mingau
rex
bidu

CONTAINER ID   IMAGEM   COMANDO   CRIADO   STATUS   NOME

REPOSITORIO   TAG      IMAGE ID       CRIADA    TAMANHO
cachorro      latest   6b8fdde4ea60   ha 4 min  555 B
gato          latest   3f9a1c2b7d10   ha 4 min  412 B
```

**O que explicar:** o `rm` apaga o registro, a pasta e o log dos containers (`-f` para os que ainda estão rodando). As **imagens continuam** — dá para criar novos containers delas a qualquer momento.

## Resumo para fechar

**Imagem = receita. Container = bolo feito da receita. Processo = bolo no forno.**

| Comando | Processos | Arquivos do container | Container |
|---|---|---|---|
| `run` | Nascem | Cópia nova da imagem | Um container **novo** |
| `stop` | Morrem | Ficam | Continua existindo (`Parado`) |
| `start` | Nascem de novo, do zero | Os mesmos de antes | O mesmo |
| `rm` | — | Apagados | Deixa de existir |
