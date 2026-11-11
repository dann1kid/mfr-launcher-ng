#!/usr/bin/env bash
# Проверка на утечки: в рабочем дереве не должно быть запрещённых образцов,
# а приватный контекст не должен попадать в индекс.
#
# Сам список образцов в репозитории не лежит и лежать не может: перечень имён
# провайдеров, доменов и идентификаторов ресурсов — это ровно то, что мы прячем.
# Он приходит переменной MFR_LEAK_PATTERNS (значением или файлом), иначе
# проверка сама стала бы утечкой.
#
# В журнал попадает только путь и номер строки. Совпавший текст не печатается
# никогда: журнал прогона доступен шире, чем репозиторий.
#
# Локально:
#
#     MFR_LEAK_PATTERNS='образец1|образец2' deploy/gitlab-ci/leak-scan.sh
#     MFR_LEAK_PATTERNS=/путь/к/файлу     deploy/gitlab-ci/leak-scan.sh

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "${SCRIPT_DIR}/lib.sh"

cd "${MFR_REPO_ROOT}"

# Проверка идёт по рабочему дереву гита: только так видно, что попало в индекс,
# а что осталось неотслеживаемым. Без дерева проверка невозможна, и молчать
# об этом нельзя.
git rev-parse --is-inside-work-tree >/dev/null 2>&1 ||
    die "проверка выполняется только в рабочем дереве гита"

violations=0

# --- образцы -----------------------------------------------------------------

# Проверка, которую нечем выполнить, не считается пройденной: без списка
# образцов задание падает, а не зеленеет.
if [ -z "${MFR_LEAK_PATTERNS:-}" ]; then
    die "не задана переменная MFR_LEAK_PATTERNS: искать нечего, а значит проверка не выполнена (см. deploy/gitlab-ci/README.md)"
fi

# Перечень образцов — это и есть то, что прячут, поэтому оба временных файла
# создаются закрытыми и убираются вместе. Права важнее, чем кажется: агент
# сборки — общая машина, и файл с правами по умолчанию читает кто угодно.
umask 077
patterns_file="$(mktemp "${TMPDIR:-/tmp}/mfr-leak-patterns.XXXXXX")"
cleaned_file="$(mktemp "${TMPDIR:-/tmp}/mfr-leak-cleaned.XXXXXX")"
trap 'rm -f "${patterns_file}" "${cleaned_file}"' EXIT

if [ -f "${MFR_LEAK_PATTERNS}" ]; then
    # Переменная типа «файл»: в значении лежит путь, а не сами образцы.
    source_kind="файл"
    cat "${MFR_LEAK_PATTERNS}" >"${patterns_file}"
else
    source_kind="значение переменной"
    printf '%s\n' "${MFR_LEAK_PATTERNS}" >"${patterns_file}"
fi

# Пустые строки и комментарии выкидываются: пустая строка как образец совпадает
# с чем угодно и превращает проверку в отказ на каждом файле.
grep -v -e '^[[:space:]]*$' -e '^[[:space:]]*#' "${patterns_file}" >"${cleaned_file}" || true
cp "${cleaned_file}" "${patterns_file}"

# Считаются строки, а не переводы строки: список без завершающего перевода
# строки иначе выглядит на одну строку короче, а список из одного образца —
# пустым, и проверка «зазеленела бы» на непустом перечне.
pattern_count="$(grep -c '' "${patterns_file}" || true)"
[ "${pattern_count}" -gt 0 ] || die "список образцов пуст (источник: ${source_kind})"

log_step "проверка на утечки"
log_info "образцов: ${pattern_count} (источник: ${source_kind}); сами образцы не печатаются"

# --- 1. приватный контекст в индексе ----------------------------------------

# В гите из context/ разрешены только карта и шаблоны. Всё остальное — это
# либо приватный файл, либо ссылка на него.
log_step "состав context/ в индексе"
while IFS= read -r path; do
    [ -n "${path}" ] || continue
    case "${path}" in
        context/README.md) continue ;;
        *.example.*) continue ;;
    esac
    log_err "приватный контекст в индексе: ${path}"
    violations=$((violations + 1))
done < <(git ls-files -- context)

if [ "${violations}" -eq 0 ]; then
    log_ok "в context/ только карта и шаблоны"
fi

# --- 2. файлы секретов в индексе --------------------------------------------

log_step "файлы секретов в индексе"
secret_files=0
while IFS= read -r path; do
    [ -n "${path}" ] || continue
    case "${path}" in
        *.example | *.example.*) continue ;;
    esac
    log_err "файл секретов в индексе: ${path}"
    secret_files=$((secret_files + 1))
done < <(git ls-files -- '*.env' '*.pem' '*.key' '*.p12' '*.jks')

violations=$((violations + secret_files))
[ "${secret_files}" -eq 0 ] && log_ok "файлов секретов в индексе нет"

# --- 3. образцы в путях ------------------------------------------------------

log_step "образцы в именах файлов"

# Тот же порядок, что и у проверки содержимого: отказ поиска — это «проверка
# не выполнена», а не «совпадений нет». Прежний `|| true` глотал код возврата,
# и на некорректном образце задание печатало «совпадений нет» — то есть
# проверка на утечки молча зеленела, ничего не проверив.
set +e
path_matches="$(git ls-files --cached --others --exclude-standard | grep -i -E -f "${patterns_file}")"
path_status=$?
set -e

# grep: 0 — нашлось, 1 — не нашлось, остальное — поиск не выполнился.
if [ "${path_status}" -gt 1 ]; then
    die "поиск по именам файлов не выполнен (код ${path_status}): проверьте синтаксис образцов"
fi

# Совпавший образец сидит в самом пути, поэтому путь маскируется: иначе
# проверка на утечки печатала бы в журнал ровно то, что ищет.
mask_script="$(mktemp "${TMPDIR:-/tmp}/mfr-leak-mask.XXXXXX")"
trap 'rm -f "${patterns_file}" "${cleaned_file}" "${mask_script}"' EXIT
sed -E 's|.*|s/&/***/gi|' "${patterns_file}" >"${mask_script}"

path_hits=0
if [ "${path_status}" -eq 0 ]; then
    while IFS= read -r path; do
        [ -n "${path}" ] || continue
        log_err "запрещённый образец в пути: $(printf '%s' "${path}" | sed -E -f "${mask_script}")"
        path_hits=$((path_hits + 1))
    done <<<"${path_matches}"
fi

violations=$((violations + path_hits))
[ "${path_hits}" -eq 0 ] && log_ok "в именах файлов совпадений нет"

# --- 4. образцы в содержимом -------------------------------------------------

log_step "образцы в содержимом рабочего дерева"

# --untracked: ищем и в неотслеживаемых файлах тоже — утечка приезжает в гит
# ровно через них. Игнорируемые файлы (build/, context/private) не смотрим:
# первые генерируются, вторые в гит не попадут по определению.
# Исключения — ровно те места, где привязка к облаку разрешена решением Р-041:
# без них проверка падала бы на законном коде выкатки и оркестратора. Список
# приходит переменной и по умолчанию пуст: молчаливых исключений быть не должно,
# каждое видно в настройках конвейера.
#
# Важно понимать границу: исключение снимает проверку образцов, но НЕ
# разрешает идентификаторы в этих файлах. Их отсутствие держится ревью,
# а не проверкой, и это единственное место, где так.
exclusions=()
if [ -n "${MFR_LEAK_ALLOWED_PATHS:-}" ]; then
    while IFS= read -r allowed; do
        [ -n "${allowed}" ] || continue
        exclusions+=(":!${allowed}")
        log_info "исключено из проверки образцов: ${allowed}"
    done < <(printf '%s\n' "${MFR_LEAK_ALLOWED_PATHS}" | tr ',' '\n' | sed 's/^ *//; s/ *$//')
fi

set +e
matches="$(git grep --no-color -I -n -i -E --untracked -f "${patterns_file}" -- . ${exclusions[@]+"${exclusions[@]}"} 2>/dev/null)"
grep_status=$?
set -e

# git grep: 0 — нашлось, 1 — не нашлось, остальное — сам поиск не выполнился.
if [ "${grep_status}" -gt 1 ]; then
    die "поиск по содержимому не выполнен (код ${grep_status}): проверьте синтаксис образцов"
fi

content_hits=0
if [ "${grep_status}" -eq 0 ]; then
    while IFS= read -r line; do
        [ -n "${line}" ] || continue
        # Из строки совпадения оставляем только путь и номер строки, а сам путь
        # маскируем: образец может сидеть и в имени файла.
        log_err "запрещённый образец: $(printf '%s' "${line}" | cut -d: -f1,2 | sed -E -f "${mask_script}")"
        content_hits=$((content_hits + 1))
    done <<<"${matches}"
fi

violations=$((violations + content_hits))
[ "${content_hits}" -eq 0 ] && log_ok "в содержимом совпадений нет"

# --- итог --------------------------------------------------------------------

if [ "${violations}" -gt 0 ]; then
    die "проверка на утечки не пройдена: совпадений ${violations}"
fi

log_step "утечек не найдено"
