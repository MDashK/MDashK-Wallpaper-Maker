@echo off
rem Compila o MDashK Wallpaper Maker Mobile e coloca o executavel em "E:\_MWPM\Build Mobile".
rem Os modelos de detecao e do waifu2x (pasta Models) e o DirectML (GPU) sao copiados para junto do executavel.
rem Cada compilacao incrementa automaticamente a versao (src\MDashKWallpaperMakerMobile\BuildNumber.txt).
setlocal
cd /d "%~dp0"
dotnet publish "src\MDashKWallpaperMakerMobile\MDashKWallpaperMakerMobile.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "Build Mobile"
if errorlevel 1 (
    echo.
    echo *** BUILD FALHOU ***
    pause
    exit /b 1
)
del /q "Build Mobile\*.lib" "Build Mobile\*.pdb" "Build Mobile\DirectML.Debug.dll" 2>nul
echo.
echo Executavel: %~dp0Build Mobile\MDashK Wallpaper Maker Mobile.exe
pause
