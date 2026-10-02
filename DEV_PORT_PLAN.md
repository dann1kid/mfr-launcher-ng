# Перенос dev-ветки (3.3.1): план и контракт

## Сеть (v2)
- База: два региона `application.server.{ruLocation,euLocation}.address`, выбор по `/ping` (5s timeout, меньший ms)
- Заголовок клиента: **X-Client-ID** (вместо Identity)
- `GET /v2/game/channels?os=WINDOWS` → `{channels:[{id}]}` — линии совместимости; первая = дефолт
- `GET /v2/game/{line}/version?os=WINDOWS` → `{id}` | 404 линии нет | 503 пока нечего
- `GET /v1/game/files?os&version&region` → `{host, files, schema, compressed_files?, compressed_schema?}`
- Манифесты: `{host}/{compressedPath}` (gzip) с fallback на `{host}/{path}` (raw):
  - **schema.proto** (Schema: version, partitions[]{id,required,files[]{mainPath,optionalPath?,sha256}}, options[]=ContentGroup{name,contents[]{name,picturePath?,description?,partition}})
  - **version.proto** (Version: id, files[]{path, storage, compressed_storage?})
- `GET /v2/launcher/version?os` → `{id}`; `GET /v2/launcher/files?os&version&region` → `{host, launcher_storage, jdk, jdk_storage}`
- `GET /ping` → доступность/скорость региона

## Загрузка файлов (HTTP, заменяет TCP-протокол)
- `.part`-файл рядом с целевым; Range: `bytes=offset-` при наличии части
- 206 → append; 416 → пусто/уже готово; 200 → заново (сервер игнорирует Range)
- Сторож бездействия: 60с без данных → DownloadStalledException (повтор попытки)
- Атомарный rename в целевое имя; storage-объекты неизменяемы — склейка безопасна
- Контроль целостности: **SHA-256** (в схеме), не MD5
- gzip-копии манифестов и файлов (compressed_storage) — когда меньше

## Клиентская логика (новое)
- Линии совместимости (channels) вместо одного билда; State.location RU/EU
- InitApplicationInitiator: выбор региона по ping, выбор линии, версии, автообновление лончера + JDK
- Лончер поставляется с JDK (launcher_storage + jdk/jdk_storage) — самозаменение учитывает
- ModifyFiles/VersionLineExtension/SchemaExtensions — утилиты новых моделей

## UI
- Кнопки Telegram + Website (новые PNG уже есть в dev), новый header.png, style.css diff
- Выбор линии в настройках; конфигурация под v2-стейт

## Проверка
- Только против dev-сервера (прод v2 не отдаёт — 404). Поднять локально: dev server/storage (H2-профиль тестов)

## Согласование с автором
- Самозамена лончера/JDK — ОТЛОЖЕНО до согласования с автором оригинала (юзер, 2026-10-02)

## Этапы
1. [текущий] Mfr.Protocol: .proto → C# (Google.Protobuf), v2 JSON DTO
2. Mfr.Core: ApiClientV2 (channels/version/files/ping), HttpFileDownloader (.part/Range/watchdog/gzip), SHA-256
3. Задачи на новых моделях (install/update/options/consistency по schema.proto)
4. Инициализация: регион/линия/версии; самозаменение лончера+JDK
5. UI: кнопки, header, выбор линии; чистка старого TCP-пути за ненадобностью
