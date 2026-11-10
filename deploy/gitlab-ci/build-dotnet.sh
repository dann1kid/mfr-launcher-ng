#!/usr/bin/env bash
# Задание build:dotnet — restore, build, test, publish одним прогоном.
# В конвейере идёт без сети: пакеты берутся из прогретого кеша образа заданий.
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "== restore =="
dotnet restore Mfr.Launcher.slnx --locked-mode

echo "== build =="
dotnet build Mfr.Launcher.slnx -c Release --no-restore -warnaserror

echo "== test =="
dotnet test Mfr.Protocol.Tests -c Release --no-build

echo "== publish =="
dotnet publish Mfr.Launcher -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    --no-build -o artifacts/launcher

# имя файла у игрока задаёт апдейтер; конвейер хранит его без скобок
mv artifacts/launcher/Mfr.Launcher.exe artifacts/launcher/MFR-Launcher.exe

echo "== адреса контура =="
if [ "${CI_COMMIT_REF_NAME:-}" = "$CI_DEFAULT_BRANCH" ] && [ -z "${CI_COMMIT_TAG:-}" ]; then
    CONTOUR=production
else
    CONTOUR=test
fi
python3 -c "
import json, sys
data = json.load(open('addresses.json'))
json.dump(data['$CONTOUR'], open('artifacts/launcher/launcher.json', 'w'), indent=2, ensure_ascii=False)
" 2>/dev/null || cp addresses.json artifacts/launcher/

echo "готово: artifacts/launcher/MFR-Launcher.exe (контур: $CONTOUR)"
