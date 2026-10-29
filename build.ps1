param([switch]$Test, [switch]$Publish, [string]$ReleaseName = 'DomainDesk-win-x64-v1.6.2')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$local = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
Push-Location $PSScriptRoot
try {
    & $dotnet build .\DomainDesk\DomainDesk.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Install the .NET 8 SDK if it is missing.' }
    if ($Test) {
        & $dotnet run --project .\DomainDesk.Tests\DomainDesk.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed.' }
    }
    if ($Publish) {
        if ($ReleaseName -notmatch '^DomainDesk-win-x64[-a-zA-Z0-9.]*$') { throw 'Invalid release folder name.' }
        $output = Join-Path $PSScriptRoot "artifacts\$ReleaseName"
        & $dotnet publish .\DomainDesk\DomainDesk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $output --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath .\README.md -Destination (Join-Path $output 'README.md')
        Copy-Item -LiteralPath .\DomainDesk\Assets\DomainDesk.ico -Destination $output
        Compress-Archive -Path "$output\*" -DestinationPath "$output.zip" -Force
        Write-Host "Portable client: $output\DomainDesk.exe"
    }
}
finally { Pop-Location }
