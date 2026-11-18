$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK não encontrado. Instale o SDK .NET 10 e execute Build.ps1 novamente.'
}

dotnet publish .\WiFiAutoControl.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou (código $LASTEXITCODE). Corrija os erros acima antes de executar o programa."
}

$exe = Join-Path $PSScriptRoot 'publish\WiFiAutoControl.exe'
if (-not (Test-Path $exe)) {
    throw "A compilação terminou sem gerar o executável esperado: $exe"
}
Write-Host "Compilação concluída: $exe" -ForegroundColor Green
