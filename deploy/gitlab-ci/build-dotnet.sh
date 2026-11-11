#!/usr/bin/env bash
# Задание build:dotnet — restore, build, test, publish одним прогоном.
# В конвейере идёт без сети: пакеты берутся из прогретого кеша образа заданий.
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "== restore =="
# restore с RID под публикацию: lock-файлы хранят оба графа — без RID и win-x64
dotnet restore Mfr.Launcher.slnx --locked-mode -r win-x64

echo "== build =="
dotnet build Mfr.Launcher.slnx -c Release --no-restore -warnaserror

echo "== test =="
dotnet test Mfr.Protocol.Tests -c Release --no-build

echo "== publish =="
# RID-граф уже восстановлен locked-restore выше; publish собирает под RID сам —
# --no-build невозможен: сборки под RID до этого шага не было
dotnet publish Mfr.Launcher -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    --no-restore \
    -o artifacts/launcher

# имя файла у игрока задаёт апдейтер; конвейер хранит его без скобок
mv artifacts/launcher/Mfr.Launcher.exe artifacts/launcher/MFR-Launcher.exe

echo "== адреса контура =="
VERSION=$(awk -F'<Version>' '/<Version>/ {split($2,a,"</Version>"); print a[1]; exit}' Directory.Build.props)
# снапшот и сборки merge request — тестовый контур, выпуск с основной ветки — боевой
if [ "${CI_COMMIT_REF_NAME:-}" = "${CI_DEFAULT_BRANCH:-master}" ] && [[ "$VERSION" != *-SNAPSHOT ]]; then
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
