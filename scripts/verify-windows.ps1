param(
    [string]$Dotnet = 'dotnet',
    [string]$ResultsDirectory = '',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root 'TestResults' }
Push-Location $root
try {
    & $Dotnet restore AITranslator.sln --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $Dotnet build AITranslator.sln -c Release --no-restore --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Full solution Release build failed.' }
    & $Dotnet test AITranslator.Tests/AITranslator.Tests.csproj -c Release --no-build --no-restore --nologo --logger 'trx;LogFileName=regression.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    if ($Publish) {
        & $Dotnet publish AITranslator/AITranslator.csproj -c Release --no-restore --self-contained false -o dist/AITranslator-Fixed --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item LICENSE dist/AITranslator-Fixed/LICENSE
        Copy-Item docs/MAINTENANCE.md dist/AITranslator-Fixed/MAINTENANCE.md
        Copy-Item docs/VERIFICATION.md dist/AITranslator-Fixed/VERIFICATION.md
    }
} finally {
    Pop-Location
}
