#!/bin/bash
# Исправляет OpenMW конфиг на Linux/Steam Deck (полная версия с контентом).
# Запуск из папки с лаунчером: curl -sL ... | bash

set -e

LAUNCHER="$(find . -maxdepth 1 -name 'MFR-Launcher*' -o -name 'MFR Launcher*' 2>/dev/null | head -1)"
if [ -n "$LAUNCHER" ]; then
    GAME_DIR="$(pwd)/game"
else
    GAME_DIR="$(pwd)"
fi

if [ ! -d "$GAME_DIR/Data Files" ]; then
    echo "ОШИБКА: 'Data Files' не найдена в $GAME_DIR"
    exit 1
fi

CONFIG_DIR="$HOME/.config/openmw"
mkdir -p "$CONFIG_DIR"

# найти пресет с полным конфигом
PRESET=""
for p in mid_PC top_PC low_PC necro_PC; do
    if [ -f "$GAME_DIR/OpenMW/OpenMW_Config/$p/openmw.cfg" ]; then
        PRESET="$GAME_DIR/OpenMW/OpenMW_Config/$p"
        break
    fi
done

if [ -z "$PRESET" ]; then
    echo "Пресет не найден, создаю минимальный конфиг"
    echo "data=\"$GAME_DIR/Data Files\"" > "$CONFIG_DIR/openmw.cfg"
else
    echo "Копирую полный конфиг из $PRESET"

    # заменить путь данных на фактический, сохранить всё остальное
    sed "s|data=\"[^\"]*\"|data=\"$GAME_DIR/Data Files\"|g" \
        "$PRESET/openmw.cfg" > "$CONFIG_DIR/openmw.cfg"

    # скопировать остальные настройки
    for f in settings.cfg input_v3.xml launcher.cfg; do
        [ -f "$PRESET/$f" ] && cp "$PRESET/$f" "$CONFIG_DIR/"
    done
fi

echo ""
echo "=== ГОТОВО ==="
echo "data=$(grep 'data=' "$CONFIG_DIR/openmw.cfg")"
echo "контент: $(grep -c 'content=' "$CONFIG_DIR/openmw.cfg" || echo 0) плагинов"
echo "groundcover: $(grep -c 'groundcover=' "$CONFIG_DIR/openmw.cfg" || echo 0)"
echo ""
echo "Запустите OpenMW через лаунчер"
