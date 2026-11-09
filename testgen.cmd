@echo off
rem Startskript for testgen (Windows). Exempel: testgen web --spec samples\shop.openapi.yaml
dotnet run --project "%~dp0testgen\src\TestGen.Cli" -- %*
