@echo off
echo Building and publishing AzIPTV for Windows x64...

:: Wipe the publish folder first so no personal test files (user.ini, etc.) sneak in.
set PUBLISH_DIR=bin\Release\net8.0\win-x64\publish
if exist "%PUBLISH_DIR%" (
    echo Cleaning %PUBLISH_DIR%...
    rd /s /q "%PUBLISH_DIR%"
)

:: Publish as a self-contained, single-file executable
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfContained=true

:: Strip the x86 VLC binaries — we target x64 only; the win-x86 folder is never loaded.
if exist "%PUBLISH_DIR%\libvlc\win-x86" (
    echo Removing unused libvlc\win-x86 folder...
    rd /s /q "%PUBLISH_DIR%\libvlc\win-x86"
)

echo.
echo Build complete! Output directory: .\bin\Release\net8.0\win-x64\publish\
echo Note: Always distribute the 'libvlc' folder and native DLLs alongside AzIPTV.exe.

:: Generate release zip  — filename: AzIPTV-v1.0.DDMMHHMM-win64.zip
for /f "tokens=*" %%i in ('powershell -NoProfile -Command "Get-Date -Format \"ddMMHHmm\""') do set TIMESTAMP=%%i
set ZIP_NAME=AzIPTV-v1.0.%TIMESTAMP%-win64.zip

if exist "%ZIP_NAME%" del /f /q "%ZIP_NAME%"

echo Creating %ZIP_NAME%...
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP_NAME%'"

echo.
echo Release archive: %ZIP_NAME%
echo.
pause