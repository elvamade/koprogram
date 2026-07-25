# MyTelegram Dev Runner - запуск без предварительной сборки
# Использует dotnet run который компилирует и запускает на лету

$ErrorActionPreference = "Stop"

# Переходим в папку source
$sourceDir = Join-Path $PSScriptRoot "..\source"
Push-Location $sourceDir

# Environment variables
$env:App__DatabaseName = "tg"
$env:App__ReadModelDatabaseName = "tg-1"
$env:App__FixedVerifyCode = "22222"
$env:Serilog__MinimumLevel__Default = "Information"
$env:App__Servers__2__Enabled = "True"
$env:App__Servers__3__Enabled = "True"

Write-Host "Starting MyTelegram services in DEV mode..." -ForegroundColor Cyan
Write-Host "Make sure MongoDB, Redis, RabbitMQ, Minio are running!" -ForegroundColor Yellow
Write-Host ""

# Запускаем сервисы в отдельных окнах
$services = @(
    @{ Name = "DataSeeder"; Path = "src/MyTelegram.DataSeeder" },
    @{ Name = "CommandServer"; Path = "src/MyTelegram.Messenger.CommandServer" },
    @{ Name = "QueryServer"; Path = "src/MyTelegram.Messenger.QueryServer" },
    @{ Name = "GatewayServer"; Path = "src/MyTelegram.GatewayServer" },
    @{ Name = "AuthServer"; Path = "src/MyTelegram.AuthServer" },
    @{ Name = "SessionServer"; Path = "src/MyTelegram.GrpcService" },
    @{ Name = "SmsSender"; Path = "src/MyTelegram.SmsSender" }
)

foreach ($svc in $services) {
    Write-Host "Starting $($svc.Name)..." -ForegroundColor Green
    $projectPath = Join-Path $sourceDir $svc.Path
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$projectPath'; dotnet run"
    Start-Sleep -Seconds 2
}

Pop-Location
Write-Host ""
Write-Host "All services started! Each runs in its own window." -ForegroundColor Cyan
