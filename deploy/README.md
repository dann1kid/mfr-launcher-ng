# Развертывание конвейера

## Раннер

Зарегистрируйте на проект один runner с docker-исполнителем (или shared runner
инстанса, если уже есть):

1. **Settings → CI/CD → Runners → New project runner**
   - Tags: `docker` (или оставьте пустым и используйте `run_untagged`)
   - Run untagged jobs: включите
2. На машине раннера установите `gitlab-runner` и выполните регистрацию:
   ```bash
   gitlab-runner register \
     --url https://mfr-project.gitlab.yandexcloud.net \
     --token <токен-из-настроек-проекта> \
     --executor docker \
     --docker-image alpine:3.20 \
     --docker-volumes /var/run/docker.sock:/var/run/docker.sock
   ```
3. Для задания `image:ci` нужен docker-in-docker (уже настроен сокетом выше)
   либо второй раннер с привилегированным режимом (`--docker-privileged`).

## Переменные конвейера

**Settings → CI/CD → Variables**:

| Переменная | Значение |
|---|---|
| `MFR_LEAK_PATTERNS` | образцы утечек через `\|` (домены провайдеров, названия хостов) — задаёт владелец |
| `MFR_LEAK_ALLOWED_PATHS` | `contract/client-api.yaml\|addresses.json` (по умолчанию) |
| `GITLAB_PUSH_TOKEN` | personal access token с `write_repository` — для `publish:tag` |

## Первый запуск

1. Запустите ручное задание **image:ci** (CI/CD → Pipelines → Run pipeline):
   оно соберёт образ с SDK и прогретым кешем пакетов.
2. После этого `build:dotnet` и `contract` пойдут на этом образе.

## Выпуск

1. Владелец снимает суффикс: `<Version>X.Y.Z</Version>` в `Directory.Build.props`.
2. Пуш в `master` → `version` пропускает, `publish:launcher` публикует exe
   в пакет проекта, `publish:tag` ставит метку `vX.Y.Z` на этот коммит.
3. Выдача игрокам — человек загружает exe из пакета через админку основного
   репозитория; регистрация записи закрытая, перед открытием объект скачивают
   из обоих регионов раздачи и сверяют по хешу с собранным.
