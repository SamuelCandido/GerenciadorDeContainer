@echo off
rem ===================================================================
rem  Job de backup: compacta a pasta "origem" num .zip datado dentro de
rem  "destino" e termina. Mantem so os %MANTER% backups mais recentes.
rem
rem  Imagem sem MiniDockerfile: a pasta inteira vira a imagem e este
rem  .bat vira o comando padrao. Para construir (na raiz do repositorio):
rem    minidocker build -t backup exemplos\backup
rem
rem  Para rodar:
rem    minidocker run --rm -v C:\MeusDocumentos:origem:ro -v backups:destino backup
rem      origem  = pasta do host, so leitura: o container nao consegue altera-la
rem      destino = volume nomeado: os zips sobrevivem ao container
rem ===================================================================

if "%MANTER%"=="" set MANTER=5

if not exist origem\ (
    echo ERRO: nada para copiar. Monte a pasta com -v PASTA:origem:ro
    exit /b 2
)
if not exist destino\ mkdir destino

rem Carimbo AAAA-MM-DD-HHMMSS a partir de %DATE% (dd/mm/aaaa) e %TIME%.
set D=%DATE:~-10%
set T=%TIME: =0%
set CARIMBO=%D:~6,4%-%D:~3,2%-%D:~0,2%-%T:~0,2%%T:~3,2%%T:~6,2%
set ARQUIVO=destino\backup-%CARIMBO%.zip

echo [%TIME:~0,8%] Compactando origem ...
tar -a -c -f "%ARQUIVO%" -C origem .
if errorlevel 1 (
    echo ERRO: falha ao compactar.
    exit /b 1
)
for %%z in ("%ARQUIVO%") do echo [%TIME:~0,8%] Criado %%~nxz com %%~zz bytes.

rem Retencao: apaga o que passar dos %MANTER% mais novos. O nome tem a
rem data em ordem AAAA-MM-DD, entao ordem alfabetica = ordem cronologica.
for /f "skip=%MANTER% delims=" %%f in ('dir /b /o-n destino\backup-*.zip') do (
    del "destino\%%f"
    echo [%TIME:~0,8%] Removido backup antigo: %%f
)

echo [%TIME:~0,8%] Backups guardados:
dir /b /o-n destino\backup-*.zip
exit /b 0
