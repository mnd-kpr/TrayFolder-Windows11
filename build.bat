@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem На сервере сборки GitHub (там задана переменная CI) окно не ждёт нажатия клавиши

rem Компилятор C# из .NET Framework 4.x — он уже есть в каждой Windows 10/11
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Не найден встроенный компилятор C# .NET Framework 4.x
    if not defined CI pause
    exit /b 1
)

rem Запущенный TrayFolder не даст перезаписать exe: закрываем его, а после сборки запускаем снова
set "RESTART="
tasklist /fi "imagename eq TrayFolder.exe" 2>nul | find /i "TrayFolder.exe" >nul
if not errorlevel 1 (
    set "RESTART=1"
    taskkill /f /im TrayFolder.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:TrayFolder.exe ^
    /reference:System.dll /reference:System.Core.dll ^
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    TrayFolder.cs
if errorlevel 1 (
    echo.
    echo Сборка не удалась. Скопируйте текст ошибки выше и пришлите его.
    if not defined CI pause
    exit /b 1
)

echo.
if defined RESTART (
    start "" "%~dp0TrayFolder.exe"
    echo Готово: TrayFolder.exe обновлён и снова запущен с вашими папками.
) else (
    echo Готово: TrayFolder.exe
    echo Положите его туда, где он будет жить постоянно, например в C:\Tools,
    echo и запустите оттуда: путь к программе запоминается для автозапуска.
)
if not defined CI pause
