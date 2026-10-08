@echo off
rem Compila o MDashK Wallpaper Maker e coloca o executavel em "E:\_MWPM\Build".
rem Os modelos de detecao e do waifu2x (pasta Models) e o DirectML (GPU) sao copiados para junto do executavel.
rem Cada compilacao incrementa automaticamente a versao (src\MDashKWallpaperMaker\BuildNumber.txt).
setlocal
cd /d "%~dp0"
dotnet publish "src\MDashKWallpaperMaker\MDashKWallpaperMaker.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "Build"
if errorlevel 1 (
    echo.
    echo *** BUILD FALHOU ***
    pause
    exit /b 1
)
del /q "Build\*.lib" "Build\*.pdb" "Build\DirectML.Debug.dll" 2>nul
echo.
echo Executavel: %~dp0Build\MDashK Wallpaper Maker.exe
pause
