# Аудит поддержки 3.3.1 (dev) в mfr-launcher-ng

Проверка «функции от 3.3.1 поддержаны, включая новые серверные ручки и расширенную спеку» —
пофайлово против `origin/dev` @ `6586ced` (3.3.1-snapshot). Ниже каждый элемент контрактa и
где он реализован в этом репозитории.

## Серверные ручки (KtorProvider → ApiClientV2 / HttpFileDownloader)

| Ручка | Параметры | Реализация |
|---|---|---|
| `GET /v2/game/{line}/version` | `X-Client-ID`, `os=WINDOWS`; 200→Found, 404→NoSuchLine, 503→NothingYet; ретраи ×5, 503 исключён из ретраев | `Mfr.Core/Network/ApiClientV2.cs` — `GetGameVersion` |
| `GET /v2/game/channels` | `X-Client-ID`, `os=WINDOWS`; порядок = порядок сервера (первая — умолчание) | `ApiClientV2.GetChannels` |
| `GET /v1/game/files?version=&os=&region=` | ссылки на манифесты + сжатые версии | `ApiClientV2.GetGameFiles` |
| `GET /v2/launcher/version` | `X-Client-ID`, `os=WINDOWS` | `ApiClientV2.GetLauncherVersion` |
| `GET /v2/launcher/files?version=&os=&region=` | host, launcher_storage, jdk, jdk_storage | `ApiClientV2.GetLauncherFiles` |
| `GET {host}/{manifest}` | gzip-манифест (.gz) с фолбэком на сырой путь | `HttpFileDownloader.DownloadManifestAsync` |
| `GET {host}/{storage}` (файлы) | потоковое скачивание, `Range: bytes=offset-` докачка (.part), 206/416/200-рестарт, сторож бездействия, атомарное переименование | `HttpFileDownloader.DownloadToFileAsync` |
| `GET /ping` (RU/EU) | выбор региона по задержке, RU приоритетен при равенстве | `ApiClientV2.ChooseRegionAsync` |

## Расширенная спека (protobuf)

`schema.proto` — 15/15 полей (partitions/options, mainPath/optionalPath, sha256, storage,
compressed_storage), `version.proto` — 5/5 полей: скопированы в `Mfr.Protocol/Proto` без
изменений, кодогенерация тем же контрактом. DTO (`ChannelList`, `Version`, `GameSchemaResponse`,
`LauncherSchemaResponse`) — имена полей дословно, `Mfr.Protocol/Dto/V2.cs`.

## Задачи и поведение

- **Линии совместимости**: выделение линии из версии первыми двумя октетами, числовое
  сравнение (`VersionLineExtension` → `V2Support.VersionLine`); установленная линия —
  `SELECTED_BUILD`, известные — `KNOWN_BUILDS`.
- **Обновление игры**: манифесты → diff по sha256 (`filesForRemove`/`filesForDownload`),
  валидация «Inconsistent files» (у каждого файла должна быть ссылка), `optionalPath`
  с флагом `applyOptionalPath` (обновление качает в основной путь, установка/переход линии
  раскладывает опциональные) — `V2/GameTasksV2.cs`.
- **Проверка целостности**: sha256 по всем файлам, пользовательский вопрос по settings-файлам
  (`AskUser`), перекачка только битого — `V2/GameTasksV2.cs` (`CheckConsistencyV2Task`).
- **Скачивание с ретраями**: до 5 попыток, база 2с × попытка, перекачиваются только
  неверифицированные файлы (`DownloadFileTask` 3.3.1 → `FileDownloadClient` v1 и
  `HttpFileDownloader` v2).
- **Новая линия**: подсветка только самой свежей линии, отклонённая молчит до появления
  следующей (`DISMISSED_BUILD`) — `V2/V2LifecycleService` + индикатор сердца в UI.
- **Статус лаунчера**: `/v2/launcher/version` → сравнение с текущей версией (`ServerLauncherVersion`).
- **Опрос сервера**: статус-поллинг раз в час (паритет с `streamUpdateSubscribe(1.hours)`),
  доступность соединения — раз в 5 минут.

## Осознанные отличия

1. **JDK не скачивается**: `launcher_update` в Java-клиенте тянет JDK и запускает
   `java.exe`; нативному клиенту JRE не нужен — при самообновлении заменяется только exe
   (in-place, через `.old`-свап, тот же механизм что у оригинального апдейтера).
   Самообновление портировано и отключено до согласования.
2. **`os=WINDOWS` захардкожен** — при мультиплатформе становится параметром (поля уже в DTO).
3. Протокол v1 (продакшен 3.2.2) остаётся рабочим путём по умолчанию; v2-слой включается,
   когда сервер начнёт отвечать на `/v2/*` — переключение без изменения данных пользователя.

## Верификация

Wire-контракт v1 закреплён golden-тестами по реальным записям обмена (`Mfr.Protocol.Tests`).
Живой прогон v2 против dev-сервера — единственный оставшийся пункт, требует его выката.
