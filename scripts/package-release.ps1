<#
.SYNOPSIS
Builds and validates versioned release archives without publishing to GitHub.
.PARAMETER FrameworkDependent
Use the installed .NET Desktop Runtime instead of bundling it.
#>
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    [xml]$properties = Get-Content Directory.Build.props
    $releaseVersion = [string]$properties.Project.PropertyGroup.Version
    if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable semantic version.' }
    & "$PSScriptRoot/build.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Release validation failed.' }
    $releaseRoot = Join-Path $projectRoot "artifacts/release/$releaseVersion"
    $stage = Join-Path $releaseRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
    $packageName = "CleanUpDesk-$releaseVersion-win-x64"
    if ($FrameworkDependent) {
        $packageName += '-framework-dependent'
        dotnet publish src/CleanUpDesk.App -c Release --self-contained false -p:CopyOutputSymbolsToPublishDirectory=false -o $stage
    } else {
        dotnet publish src/CleanUpDesk.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:CopyOutputSymbolsToPublishDirectory=false -o $stage
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; no archive was created.' }
    Copy-Item LICENSE, README.md, THIRD-PARTY-NOTICES.md $stage
    Copy-Item "docs/releases/$releaseVersion.md" (Join-Path $stage 'RELEASE-NOTES.md')
    Copy-Item -Recurse licenses $stage
    $archive = Join-Path $releaseRoot "$packageName.zip"
    Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -Encoding Ascii (Join-Path $releaseRoot "$packageName.sha256")
    Get-ChildItem -LiteralPath $releaseRoot -Filter '*.sha256' | Sort-Object Name | Get-Content | Set-Content -Encoding Ascii (Join-Path $releaseRoot 'SHA256SUMS.txt')
    Write-Output "Ready for review: $archive"
    Write-Output 'No commit, tag, push, or GitHub release was created.'
} finally { Pop-Location }
