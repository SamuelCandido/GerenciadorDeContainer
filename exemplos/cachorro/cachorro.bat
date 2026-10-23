@echo off
rem Roda dentro do container. O arquivo vezes.txt fica no rootfs do
rem container: sobrevive ao stop/start, mas some no rm.
set VEZES=0
if exist vezes.txt set /p VEZES=<vezes.txt
set /a VEZES+=1
>vezes.txt echo %VEZES%

echo.
echo     / \__
echo    (    @\___
echo    /         O
echo   /   (_____/
echo  /_____/   U
echo.
echo %NOME% acordou! Este container ja foi iniciado %VEZES% vez(es).

set LATIDOS=0
:latir
set /a LATIDOS+=1
echo [%TIME:~0,8%] %NOME%: au au! (latido %LATIDOS%)
ping -n 3 127.0.0.1 > nul
goto latir
