@echo off
rem ===================================================================
rem  Painel web com os relatorios gerados pelo processador.
rem
rem  Imagem sem MiniDockerfile: este .bat vira o comando padrao.
rem    minidocker build -t painel exemplos\painel
rem    minidocker run -d --name painel -p 8080:80 -v C:\demo\fila:fila:ro painel
rem    abra http://localhost:8080
rem
rem  O servidor escuta na porta que o MiniDocker entrega em %PORT%; quem
rem  publica a 8080 do host e o proxy do MiniDocker (-p 8080:80).
rem ===================================================================

if "%PORT%"=="" (
    echo ERRO: nenhuma porta publicada. Rode com -p 8080:80
    exit /b 2
)

rem O PowerShell espera estas pastas do usuario. O container nao herda o
rem ambiente do host, entao elas ficam dentro do proprio rootfs.
set USERPROFILE=%CD%
set APPDATA=%CD%\AppData\Roaming
set LOCALAPPDATA=%CD%\AppData\Local
if not exist "%APPDATA%" mkdir "%APPDATA%"
if not exist "%LOCALAPPDATA%" mkdir "%LOCALAPPDATA%"

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File painel.ps1
