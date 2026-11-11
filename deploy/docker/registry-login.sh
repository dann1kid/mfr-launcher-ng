#!/usr/bin/env bash
# Вход в реестр образов перед публикацией.
#
# Двух способов входа достаточно, и они выбираются переменной, а не угадыванием:
#
#   basic      логин и пароль из переменных окружения. Простой путь, но пароль
#              приходится где-то хранить.
#   metadata   учётные данные берутся у среды исполнения: исполнитель получает
#              короткоживущий токен по своей учётной записи, и хранить нечего.
#              Предпочтительный способ — секрета, который может утечь, просто
#              не существует.
#
# Адрес службы метаданных, имя поля с токеном и заголовок запроса приходят
# переменными: это параметры среды, а не константы репозитория.
#
# Переменные:
#   MFR_IMAGE_REGISTRY        префикс реестра со слэшем на конце (обязательна)
#   MFR_REGISTRY_AUTH         basic | metadata (обязательна)
#   MFR_REGISTRY_USER         имя пользователя; для metadata по умолчанию iam
#   MFR_REGISTRY_PASSWORD     пароль — только для basic (секрет, маскируется)
#   MFR_REGISTRY_TOKEN_URL    адрес службы метаданных — только для metadata
#   MFR_REGISTRY_TOKEN_HEADER заголовок запроса к службе метаданных
#   MFR_REGISTRY_TOKEN_FIELD  поле с токеном в ответе, по умолчанию access_token
#
# Коды возврата: 0 — вход выполнен, 1 — ошибка.

set -euo pipefail

log() { printf '  %s\n' "$*"; }
fail() {
    printf 'ошибка: %s\n' "$*" >&2
    exit 1
}

[ -n "${MFR_IMAGE_REGISTRY:-}" ] || fail "не задана переменная MFR_IMAGE_REGISTRY"
[ -n "${MFR_REGISTRY_AUTH:-}" ] || fail "не задана переменная MFR_REGISTRY_AUTH (basic|metadata)"

# Хост реестра — это префикс без пути и без завершающего слэша: docker login
# принимает именно хост, а не адрес образа.
registry_host="${MFR_IMAGE_REGISTRY%%/*}"
[ -n "${registry_host}" ] || fail "из MFR_IMAGE_REGISTRY не выделяется хост реестра"

case "${MFR_REGISTRY_AUTH}" in
    basic)
        [ -n "${MFR_REGISTRY_USER:-}" ] || fail "для входа basic нужна переменная MFR_REGISTRY_USER"
        [ -n "${MFR_REGISTRY_PASSWORD:-}" ] || fail "для входа basic нужна переменная MFR_REGISTRY_PASSWORD"
        user="${MFR_REGISTRY_USER}"
        secret="${MFR_REGISTRY_PASSWORD}"
        ;;
    metadata)
        [ -n "${MFR_REGISTRY_TOKEN_URL:-}" ] || fail "для входа metadata нужна переменная MFR_REGISTRY_TOKEN_URL"
        user="${MFR_REGISTRY_USER:-iam}"
        header="${MFR_REGISTRY_TOKEN_HEADER:-Metadata-Flavor: Google}"
        field="${MFR_REGISTRY_TOKEN_FIELD:-access_token}"

        # Ответ службы метаданных не печатается ни при каком исходе: он
        # содержит действующий токен. Наружу идёт только факт неудачи.
        response="$(curl --silent --fail --max-time 10 \
            --header "${header}" "${MFR_REGISTRY_TOKEN_URL}" 2>/dev/null)" ||
            fail "служба метаданных не отдала токен: проверь, что у исполнителя есть учётная запись"

        secret="$(printf '%s' "${response}" |
            sed -n "s/.*\"${field}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p")"
        [ -n "${secret}" ] || fail "в ответе службы метаданных нет поля ${field}"
        ;;
    *) fail "неизвестный способ входа: ${MFR_REGISTRY_AUTH} (ожидается basic или metadata)" ;;
esac

# Пароль уходит на stdin, а не аргументом: список процессов агента виден шире,
# чем переменные задания.
printf '%s' "${secret}" | docker login --username "${user}" --password-stdin "${registry_host}" >/dev/null 2>&1 ||
    fail "вход в реестр отклонён (способ ${MFR_REGISTRY_AUTH}, пользователь ${user})"

log "вход в реестр выполнен, способ ${MFR_REGISTRY_AUTH}"
