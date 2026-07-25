@echo off
REM MyTelegram Dev Runner - запуск без предварительной сборки
REM Использует dotnet run который компилирует и запускает на лету

echo Starting MyTelegram services in DEV mode...
echo Make sure MongoDB, Redis, RabbitMQ, Minio are running!
echo.

REM Environment variables
set App__DatabaseName=tg
set App__ReadModelDatabaseName=tg-1
set App__FixedVerifyCode=22222
set Serilog__MinimumLevel__Default=Information
set App__Servers__2__Enabled=True
set App__Servers__3__Enabled=True

cd /d %~dp0..\source

echo Starting DataSeeder...
start "DataSeeder" cmd /k "cd src\MyTelegram.DataSeeder && dotnet run"
timeout /t 3 /nobreak > nul

echo Starting CommandServer...
start "CommandServer" cmd /k "cd src\MyTelegram.Messenger.CommandServer && dotnet run"
timeout /t 2 /nobreak > nul

echo Starting QueryServer...
start "QueryServer" cmd /k "cd src\MyTelegram.Messenger.QueryServer && dotnet run"
timeout /t 2 /nobreak > nul

echo Starting GatewayServer...
start "GatewayServer" cmd /k "cd src\MyTelegram.GatewayServer && dotnet run"
timeout /t 2 /nobreak > nul

echo Starting AuthServer...
start "AuthServer" cmd /k "cd src\MyTelegram.AuthServer && dotnet run"
timeout /t 2 /nobreak > nul

echo Starting SessionServer (GrpcService)...
start "SessionServer" cmd /k "cd src\MyTelegram.GrpcService && dotnet run"
timeout /t 2 /nobreak > nul

echo Starting SmsSender...
start "SmsSender" cmd /k "cd src\MyTelegram.SmsSender && dotnet run"

echo.
echo All services started! Each runs in its own window.
echo.
pause
