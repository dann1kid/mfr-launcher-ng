#!/usr/bin/env bash
# Задание version — правила цикла выпуска (AGENTS.md «Версия и выпуск»).
#   <Version> без суффикса и метки vX.Y.Z нет  → выпуск закрыт: ветку отвергаем
#   <Version> без суффикса и метка есть        → выпуск уже состоялся, писать поверх нельзя
#   <Version> со с suffixом -SNAPSHOT          → цикл открыт, пишем свободно
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props)
TAG="v$VERSION"

echo "версия: $VERSION"

if [[ "$VERSION" == *-SNAPSHOT ]]; then
    echo "цикл открыт — ветка разрешена"
    exit 0
fi

# метка на этом коммите? (выпуск мог уже состояться этим же пушем)
if git rev-parse "$TAG" >/dev/null 2>&1 && [ "$(git rev-parse "$TAG")" = "$(git rev-parse HEAD)" ]; then
    echo "выпуск уже помечен — задание publish:tag поставило метку"
    exit 0
fi

if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "ОШИБКА: метка $TAG уже стоит на другом коммите — писать поверх выпущенной версии нельзя"
    exit 1
fi

# метки ещё нет: выпуск состоится этим пушем в master — разрешаем только master
if [ "${CI_COMMIT_REF_NAME:-}" = "${CI_DEFAULT_BRANCH:-master}" ]; then
    echo "выпуск: $TAG будет опубликован этим пушем"
    exit 0
fi

echo "ОШИБКА: версия без суффикса (выпуск закрыт) в ветке не-master — откройте цикл суффиксом -SNAPSHOT"
exit 1
