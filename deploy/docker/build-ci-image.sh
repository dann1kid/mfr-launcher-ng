#!/usr/bin/env bash
# Сборка образа заданий: SDK и прогретый кеш пакетов (Dockerfile.ci).
#
#   deploy/docker/build-ci-image.sh [--push]
#
# Образ проверяется restore без сети — тем же набором, что идёт в конвейере.
# Не прошёл — в кеше чего-то нет, и конвейер на этом образе пошёл бы за
# пакетами наружу; такой образ не публикуется.
#
# Меток две: версионная MFR_CI_IMAGE_TAG — точка отката, подвижная latest —
# её берут переменные конвейера и машина сборки. Откат — перестановка latest
# на прежнюю версионную метку.
#
# Переменные: MFR_IMAGE_REGISTRY (префикс реестра со слэшем), MFR_CI_IMAGE_TAG,
# для --push ещё MFR_REGISTRY_AUTH и параметры входа (registry-login.sh).
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

fail() {
    printf 'ошибка: %s\n' "$*" >&2
    exit 1
}

PUSH=false
case "${1:-}" in
    --push) PUSH=true ;;
    "") ;;
    *) fail "неизвестный аргумент: $1" ;;
esac

[ -n "${MFR_IMAGE_REGISTRY:-}" ] || fail "не задана переменная MFR_IMAGE_REGISTRY"
[ -n "${MFR_CI_IMAGE_TAG:-}" ] || fail "не задана переменная MFR_CI_IMAGE_TAG"
NAME="${MFR_IMAGE_REGISTRY}${MFR_CI_IMAGE_NAME:-mfr-ci-dotnet}"

echo "== сборка ${NAME}:${MFR_CI_IMAGE_TAG} =="
docker build --platform linux/amd64 --file "${SCRIPT_DIR}/Dockerfile.ci" \
    --tag "${NAME}:${MFR_CI_IMAGE_TAG}" --tag "${NAME}:latest" "${REPO_ROOT}"

echo "== проверка restore без сети =="
# Исходники кладутся копированием, а не монтированием: Docker здесь — сокет
# машины, и путь задания для него не существует.
check="mfr-ci-dotnet-check-${MFR_CI_IMAGE_TAG}"
docker rm --force "${check}" >/dev/null 2>&1 || true
docker create --name "${check}" --network none --workdir /check "${NAME}:${MFR_CI_IMAGE_TAG}" \
    bash -c 'dotnet restore Mfr.Launcher.slnx --locked-mode -p:NuGetAudit=false \
        && dotnet restore Mfr.Launcher -r win-x64 -p:SelfContained=true -p:NuGetAudit=false' >/dev/null
tar -cf - -C "${REPO_ROOT}" --exclude ./.git --exclude '*/bin' --exclude '*/obj' --exclude ./artifacts . |
    docker cp - "${check}:/check"
status=0
docker start --attach "${check}" || status=$?
docker rm --force "${check}" >/dev/null 2>&1 || true
[ "${status}" -eq 0 ] || fail "restore без сети не прошёл: в кеше образа не хватает пакетов"

if [ "${PUSH}" = true ]; then
    "${SCRIPT_DIR}/registry-login.sh"
    docker push "${NAME}:${MFR_CI_IMAGE_TAG}"
    docker push "${NAME}:latest"
    echo "опубликовано: ${MFR_CI_IMAGE_TAG} и latest"
fi
