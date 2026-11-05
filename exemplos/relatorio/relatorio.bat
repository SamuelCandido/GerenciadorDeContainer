@echo off
rem Ponto de entrada do container: o CMD da imagem chama este arquivo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File relatorio.ps1
exit /b %ERRORLEVEL%
