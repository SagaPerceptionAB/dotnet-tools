@echo off
rem Startskript for testgen (Windows). Exempel: testgen ui --spec samples\shop.openapi.yaml
dotnet run --project "%~dp0testgen\src\TestGen.Cli" -- %*
