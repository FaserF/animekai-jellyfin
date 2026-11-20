# Local build & test script for Jellyfin.Plugin.Rezero
$ErrorActionPreference = "Stop"

if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $env:PATH = "$env:USERPROFILE\.dotnet;" + $env:PATH
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks) {
        Write-Host "Running local build via installed .NET SDK..." -ForegroundColor Cyan
        dotnet restore
        dotnet build -c Release --no-restore
        dotnet run --project Jellyfin.Plugin.Rezero.Tests -c Release --no-build
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Local build & tests completed successfully!" -ForegroundColor Green
            exit 0
        }
    }
}

Write-Host "Running local build via mcr.microsoft.com/dotnet/sdk:10.0..." -ForegroundColor Cyan
$srcDir = $PWD.Path.Replace("\", "/")
docker run --rm -v "${srcDir}:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -c "dotnet restore && dotnet build -c Release && dotnet run --project Jellyfin.Plugin.Rezero.Tests -c Release --no-build"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Local build & tests completed successfully!" -ForegroundColor Green
} else {
    Write-Host "Build or tests failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
