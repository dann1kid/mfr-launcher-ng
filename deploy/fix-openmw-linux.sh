#!/bin/bash
# Исправляет OpenMW конфиг на Linux/Steam Deck.
# Запуск: curl -sL https://raw.githubusercontent.com/dann1kid/mfr-launcher-ng/master/deploy/fix-openmw-linux.sh | bash
# Или: скачайте и запустите из папки с игрой.

set -e

# найти папку игры (рядом с лаунчером или текущая папка)
LAUNCHER="$(find . -maxdepth 1 -name 'MFR-Launcher*' -o -name 'MFR Launcher*' 2>/dev/null | head -1)"
if [ -n "$LAUNCHER" ]; then
    GAME_DIR="$(pwd)/game"
else
    GAME_DIR="$(pwd)"
fi

if [ ! -d "$GAME_DIR/Data Files" ]; then
    echo "ОШИБКА: папка 'Data Files' не найдена в $GAME_DIR"
    echo "Запустите из папки где лежит лаунчер, или укажите путь: GAME_DIR=/путь/к/игре $0"
    exit 1
fi

CONFIG_DIR="$HOME/.config/openmw"
mkdir -p "$CONFIG_DIR"

# скопировать настройки из пресета (если есть)
PRESET=""
for p in mid_PC top_PC low_PC necro_PC; do
    if [ -d "$GAME_DIR/OpenMW/OpenMW_Config/$p" ]; then
        PRESET="$GAME_DIR/OpenMW/OpenMW_Config/$p"
        break
    fi
done

if [ -n "$PRESET" ]; then
    echo "Копирую настройки из пресета: $PRESET"
    for f in settings.cfg input_v3.xml; do
        [ -f "$PRESET/$f" ] && cp "$PRESET/$f" "$CONFIG_DIR/"
    done
fi

# сгенерировать openmw.cfg с правильным путём
echo "data=\"$GAME_DIR/Data Files\"" > "$CONFIG_DIR/openmw.cfg"

echo ""
echo "=== ГОТОВО ==="
echo "Конфиг: $CONFIG_DIR/openmw.cfg"
echo "Путь к данным: $GAME_DIR/Data Files"
echo ""
echo "Проверка:"
grep "data=" "$CONFIG_DIR/openmw.cfg"
ls "$GAME_DIR/Data Files/" | head -3
echo ""
echo "Запустите OpenMW через лаунчер или напрямую:"
echo "  $GAME_DIR/OpenMW/openmw"
