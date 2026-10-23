@echo off
rem Roda dentro do container, uma vez, e termina com codigo 0.
echo.
echo     /\_/\
echo    ( o.o )
echo     ^> ^^ ^<
echo.
set /p CONSTRUIDA=<construida_em.txt
echo %NOME%: miau! Agora sao %TIME:~0,8%.
echo (esta imagem foi construida em %CONSTRUIDA%)
echo %NOME% foi dormir. Fim do container.
exit /b 0
