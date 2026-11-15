@echo off
rem ===================================================================
rem  Worker que analisa arquivos de log. Fica rodando e, a cada
rem  %INTERVALO% segundos, olha a fila:
rem
rem    fila\entrada\              chegam os .log/.txt (quem coloca e o host)
rem    fila\trabalhando\REPLICA\  o arquivo enquanto uma replica o analisa
rem    fila\saida\                um relatorio por arquivo: linhas, erros, avisos
rem    fila\processados\          o original, depois de analisado
rem
rem  A fila e uma pasta do host montada como volume:
rem    minidocker run -d --name worker -v C:\fila:fila processador
rem  Varias replicas podem dividir a mesma fila:
rem    minidocker run -d --replicas 3 --name worker -v C:\fila:fila processador
rem ===================================================================

if "%INTERVALO%"=="" set INTERVALO=2
if "%HOSTNAME%"=="" set HOSTNAME=worker
set /a ESPERA=INTERVALO+1
set VERSAO=desconhecida
if exist versao.txt set /p VERSAO=<versao.txt

for %%d in (entrada saida processados trabalhando\%HOSTNAME%) do if not exist fila\%%d\ mkdir fila\%%d 2>nul
if not exist fila\trabalhando\%HOSTNAME%\ (
    echo [%TIME:~0,8%] ERRO: nao foi possivel criar as pastas da fila. Monte uma pasta gravavel com -v PASTA:fila
    exit /b 2
)

set TOTAL=0
echo [%TIME:~0,8%] %HOSTNAME% iniciado. Imagem construida em %VERSAO%
echo [%TIME:~0,8%] %HOSTNAME% vigiando fila\entrada a cada %INTERVALO% s.

:vigiar
for %%f in (fila\entrada\*.log fila\entrada\*.txt) do call :reivindicar "%%~f"
ping -n %ESPERA% 127.0.0.1 > nul
goto vigiar

rem Varias replicas olham a mesma pasta e podem ver o mesmo arquivo. O move
rem dentro do mesmo disco e um rename no NTFS, uma operacao atomica: so uma
rem replica consegue tirar o arquivo da entrada; as outras recebem erro e
rem pulam. E exclusao mutua entre containers usando o sistema de arquivos.
:reivindicar
move "%~1" "fila\trabalhando\%HOSTNAME%\" > nul 2>&1 || goto :eof
call :processar "fila\trabalhando\%HOSTNAME%\%~nx1"
goto :eof

:processar
set LINHAS=0
set ERROS=0
set AVISOS=0
for /f %%n in ('find /c /v "" ^< "%~1"') do set LINHAS=%%n
for /f %%n in ('find /c /i "erro" ^< "%~1"') do set ERROS=%%n
for /f %%n in ('find /c /i "aviso" ^< "%~1"') do set AVISOS=%%n

> "fila\saida\%~n1.relatorio.txt" (
    echo arquivo:    %~nx1
    echo tamanho:    %~z1 bytes
    echo linhas:     %LINHAS%
    echo erros:      %ERROS%
    echo avisos:     %AVISOS%
    echo analisado:  %DATE% %TIME:~0,8%
    echo por:        %HOSTNAME%
)
move /y "%~1" "fila\processados\" > nul

set /a TOTAL+=1
echo [%TIME:~0,8%] %HOSTNAME%: %~nx1: %LINHAS% linhas, %ERROS% erros, %AVISOS% avisos. Total desta replica: %TOTAL%
goto :eof
