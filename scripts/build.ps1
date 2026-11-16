<#
.SYNOPSIS
Builds CleanUpDesk and runs both isolated regression harnesses.
.DESCRIPTION
Stops on restore, build, or test failure. Publishing creates a self-contained
Windows x64 archive; it does not create a repository, release, or tag.
.PARAMETER Publish
Publish and archive the application after successful validation.
#>
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet restore CleanUpDesk.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build CleanUpDesk.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project scripts/DocumentationAudit -- .
    if ($LASTEXITCODE -ne 0) { throw 'Source documentation audit failed.' }
    dotnet run --project tests/CleanUpDesk.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed; packaging was stopped.' }
    dotnet run --project tests/CleanUpDesk.UiTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'WPF tests failed; packaging was stopped.' }
    if ($Publish) {
        dotnet publish src/CleanUpDesk.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
        Copy-Item LICENSE artifacts/win-x64/LICENSE
        Copy-Item README.md artifacts/win-x64/README.md
        Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/CleanUpDesk-win-x64.zip -Force
    }
} finally { Pop-Location }
