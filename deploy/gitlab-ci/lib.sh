#!/usr/bin/env bash
# Общие функции скриптов конвейера сборки.
#
# Скрипты конвейера пишутся так, чтобы их можно было запустить руками из корня
# репозитория: падение в конвейере обязано воспроизводиться локально той же
# командой, иначе разбираться придётся правкой .yml и повторными прогонами.
#
# Ни одного адреса, идентификатора и ключа: всё приходит переменными окружения.

# Корень репозитория вычисляется от расположения файла, а не от текущего
# каталога: скрипт зовут и из корня, и из задания конвейера.
MFR_CI_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MFR_REPO_ROOT="$(cd -- "${MFR_CI_DIR}/../.." && pwd)"
export MFR_CI_DIR MFR_REPO_ROOT

# Цвет отключается, когда вывод идёт не в терминал: в журнале конвейера
# управляющие последовательности только мешают.
if [ -t 1 ] && [ "${MFR_CI_NO_COLOR:-false}" != "true" ]; then
    _c_step=$'\033[1;34m'
    _c_ok=$'\033[0;32m'
    _c_warn=$'\033[0;33m'
    _c_err=$'\033[0;31m'
    _c_off=$'\033[0m'
else
    _c_step='' _c_ok='' _c_warn='' _c_err='' _c_off=''
fi

log_step() { printf '%s==>%s %s\n' "${_c_step}" "${_c_off}" "$*"; }
log_info() { printf '    %s\n' "$*"; }
log_ok() { printf '%s  ok%s %s\n' "${_c_ok}" "${_c_off}" "$*"; }
log_warn() { printf '%sвнимание%s %s\n' "${_c_warn}" "${_c_off}" "$*" >&2; }
log_err() { printf '%sошибка%s %s\n' "${_c_err}" "${_c_off}" "$*" >&2; }

die() {
    log_err "$*"
    exit 1
}

# Проверка обязательных переменных. Называются все недостающие сразу: чинить
# конфигурацию конвейера по одной переменной за прогон — это по прогону
# на переменную.
require_vars() {
    local missing=() name
    for name in "$@"; do
        if [ -z "${!name:-}" ]; then
            missing+=("${name}")
        fi
    done
    if [ ${#missing[@]} -gt 0 ]; then
        die "не заданы переменные конвейера: ${missing[*]} (см. deploy/gitlab-ci/README.md)"
    fi
}

# Версия проекта — из gradle.properties, единственного места, где она задана.
# Отдельного «версионного» файла в конвейере не заводим: две правды разъедутся.
project_version() {
    local value
    value="$(awk -F= '/^version=/ { print $2; exit }' "${MFR_REPO_ROOT}/gradle.properties")"
    [ -n "${value}" ] || die "в gradle.properties не найдено значение version"
    printf '%s\n' "${value}"
}

# Целевая ОС агента в терминах задачи упаковки. Кросс-компиляции у штатной
# упаковки нет, поэтому единственный правильный ответ — та ОС, на которой
# задание уже выполняется.
current_os() {
    case "$(uname -s)" in
        Darwin) printf 'macos\n' ;;
        Linux) printf 'linux\n' ;;
        MINGW* | MSYS* | CYGWIN*) printf 'windows\n' ;;
        *) die "неизвестная ОС агента: $(uname -s)" ;;
    esac
}
