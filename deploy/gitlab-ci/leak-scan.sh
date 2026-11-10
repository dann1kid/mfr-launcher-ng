#!/usr/bin/env bash
# Задание leak-scan — образцы утечек приходят переменными конвейера
# MFR_LEAK_PATTERNS и MFR_LEAK_ALLOWED_PATHS (их принимает общий скрипт
# основного репозитория; здесь — локальная проверка теми же переменными).
# Печатает только путь и номер строки; совпавший текст маскируется.
set -euo pipefail
cd "$(dirname "$0")/../.."

PATTERNS="${MFR_LEAK_PATTERNS:-}"
ALLOWED="${MFR_LEAK_ALLOWED_PATHS:-contract/client-api.yaml|addresses.json}"

if [ -z "$PATTERNS" ]; then
    echo "leak-scan: MFR_LEAK_PATTERNS не задан — проверка пропущена (настройте переменные конвейера)"
    exit 0
fi

# образцы — через | ; разрешённые пути — через |
IFS='|' read -ra PATTERNS_ARR <<< "$PATTERNS"
IFS='|' read -ra ALLOWED_ARR <<< "$ALLOWED"

BUILD_RULES=""
for p in "${PATTERNS_ARR[@]}"; do
    [ -n "$p" ] && BUILD_RULES="$BUILD_RULES -e \"$p\""
done

FOUND=0
while IFS= read -r file; do
    rel="${file#./}"
    SKIP=0
    for a in "${ALLOWED_ARR[@]}"; do
        [ -n "$a" ] && [[ "$rel" == $a ]] && SKIP=1 && break
    done
    [ "$SKIP" = 1 ] && continue
    while IFS=: read -r lineno match; do
        [ -z "$lineno" ] && continue
        echo "leak-scan: $rel:$lineno (совпадение замаскировано)"
        FOUND=$((FOUND + 1))
    done < <(grep -En $BUILD_RULES "$file" 2>/dev/null | cut -d: -f1,3- || true)
done < <(git ls-files 2>/dev/null || find . -type f \
    -not -path './.git/*' -not -path '*/bin/*' -not -path '*/obj/*' \
    -not -path './artifacts/*' -not -path './publish*/*')

if [ "$FOUND" -gt 0 ]; then
    echo "leak-scan: найдено $FOUND совпадений"
    exit 1
fi
echo "leak-scan: чисто"
