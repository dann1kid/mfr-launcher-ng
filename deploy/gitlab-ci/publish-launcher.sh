#!/usr/bin/env bash
# Задание publish:launcher — публикация неизменяемого exe в пакет проекта.
# Выдача игрокам (регистрация записи в админке) — отдельный шаг человека.
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION=$(awk -F'<Version>' '/<Version>/ {split($2,a,"</Version>"); print a[1]; exit}' Directory.Build.props)
if [[ "$VERSION" == *-SNAPSHOT ]]; then
    echo "снапшот $VERSION — публикация дистрибутива не выполняется"
    exit 0
fi

# exe уже собран заданием build:dotnet в artifacts/launcher/
FILE="artifacts/launcher/MFR-Launcher.exe"
[ -f "$FILE" ] || { echo "ОШИБКА: $FILE не найден — задание build:dotnet не отработало"; exit 1; }

SHA256=$(sha256sum "$FILE" | cut -d' ' -f1)
echo "публикация: $FILE (версия $VERSION, sha256 $SHA256)"

# пакет проекта: Generic Package Registry текущего проекта
# (загрузку в объектное хранилище раздачи выполняет человек через админку)
if [ -n "${CI_API_V4_URL:-}" ] && [ -n "${CI_PROJECT_ID:-}" ]; then
    curl --fail-with-body -sS -X POST \
        --header "JOB-TOKEN: ${CI_JOB_TOKEN}" \
        --form "file=@$FILE" \
        "${CI_API_V4_URL}/projects/${CI_PROJECT_ID}/packages/generic/mfr-launcher/${VERSION}/MFR-Launcher.exe" \
        && echo "пакет опубликован в реестре проекта" \
        || echo "ПРЕДУПРЕЖДЕНИЕ: реестр недоступен, exe остался в артефактах конвейера"
else
    echo "локальный прогон: реестр недоступен, exe в artifacts/"
fi
