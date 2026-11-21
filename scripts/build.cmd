@echo off
rem 流光影院编译脚本：当前终端可能缺少 ProgramFiles(x86) 等变量，NuGet 需要它们
set "ProgramFiles(x86)=C:\Program Files (x86)"
if not defined ProgramFiles set "ProgramFiles=C:\Program Files"
if not defined ProgramW6432 set "ProgramW6432=C:\Program Files"
if not defined ProgramData set "ProgramData=C:\ProgramData"
"%~dp0..\.dotnet\dotnet.exe" build "%~dp0..\src\TV\TV.csproj" -c Release -v minimal --nologo %*
