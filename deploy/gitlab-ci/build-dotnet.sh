#!/usr/bin/env bash
# Задание build:dotnet — restore, build, test, publish одним прогоном.
# Пакеты restore берёт из источника в nuget.config; набор закреплён lock-файлами.
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "== restore =="
dotnet restore Mfr.Launcher.slnx --locked-mode

echo "== build =="
dotnet build Mfr.Launcher.slnx -c Release --no-restore -warnaserror

echo "== test =="
dotnet test Mfr.Protocol.Tests -c Release --no-build

echo "== publish =="
# Своя сборка под RID: build выше шёл без него, и --no-build здесь не найдёт
# ресурсов под win-x64. Lock-файлы описывают сборку без RID, поэтому restore
# публикации идёт без --locked-mode.
dotnet publish Mfr.Launcher -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/launcher

# имя файла у игрока задаёт апдейтер; конвейер хранит его без скобок
mv artifacts/launcher/Mfr.Launcher.exe artifacts/launcher/MFR-Launcher.exe

echo "готово: artifacts/launcher/MFR-Launcher.exe"
