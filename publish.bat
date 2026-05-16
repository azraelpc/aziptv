@echo off
echo Building and publishing AzIPTV for Windows x64...

:: Publish as a self-contained, single-file executable
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfContained=true

echo.
echo Build complete! Output directory: .\bin\Release\net8.0\win-x64\publish\
echo Note: Always distribute the 'libvlc' folder and native DLLs alongside AzIPTV.exe.
echo.
pause