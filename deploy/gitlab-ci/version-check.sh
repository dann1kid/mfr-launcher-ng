#!/usr/bin/env bash
# Задание version — правила цикла выпуска (AGENTS.md «Версия и выпуск»).
#   <Version> со с suffixом -SNAPSHOT          → цикл открыт, пишем свободно
#   <Version> без суффикса и метки vX.Y.Z нет  → готовящийся выпуск: разрешаем,
#                                               в том числе в merge request —
#                                               прямые пуш и вливания в основную
#                                               ветку закрыты, MR и есть штатный
#                                               путь выпуска
#   <Version> без суффикса, метка на коммите   → выпуск уже состоялся, писать поверх нельзя
#   <Version> без суффикса, метка на другом    → поверх выпущенной версии нельзя
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION=$(awk -F'<Version>' '/<Version>/ {split($2,a,"</Version>"); print a[1]; exit}' Directory.Build.props)
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

# метки ещё нет: готовящийся выпуск. Релизный merge request — штатный путь:
# конвейер его проверяет, публикация и метка ставятся заданиями на основной ветке
echo "выпуск: $TAG будет опубликован при появлении в ${CI_DEFAULT_BRANCH:-master}"
exit 0
