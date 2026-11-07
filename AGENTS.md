# AGENTS.md — правила работы с репозиторием

Нативный (C#/.NET 10 + Avalonia) лаунчер Morrowind Fullrest Repack. Порт Java/Kotlin-клиента
(эталон: `Lezenford/mfr-launcher`, ветка `dev` = 3.3.x/v2, `master` = 3.1.x/v1) с сохранением
wire-контракта 1:1. Это не вдохновение, а построчный порт: любое изменение поведения сверяется
с Kotlin-источником.

## Структура

- `Mfr.Protocol` — wire-контракт: v1 REST DTO + TCP-кодек (7 сообщений, length-prefix + JSON),
  protobuf v2 (`Proto/*.proto` — копии из dev без правок), v2 DTO, golden-тесты по реальным
  записям обмена (`Mfr.Protocol.Tests/Golden`).
- `Mfr.Core` — домен: задачи (`Tasks/` = v1, `V2/` = v2), сеть (`ApiServerClient`/`FileDownloadClient`
  = v1, `ApiClientV2`/`HttpFileDownloader` = v2), SQLite-хранилище (`Storage/`, схема повторяет H2),
  сервисы (`Services/`).
- `Mfr.Launcher` — Avalonia UI, скин 1:1 (`Skin.axaml`, ассеты в `Assets/`).
- `Mfr.Smoke` — живые пробы против сервера (`dotnet run --project Mfr.Smoke -- rest|file|...`).

## Непреложные правила

1. **Wire-контракт заморожен.** Изменять форматы сообщений, поля DTO, proto-файлы нельзя —
   сервер их уже отдаёт/ждёт. Новые поля — только если их добавил сервер.
2. **Поведение = Kotlin-эталон.** Нашли расхождение — читаем Kotlin-источник, переносим его
   семантику, а не «придумываем похожее». Порядок статусов кнопки, ретраи, приоритеты — всё
   из `LauncherController`/`InitApplicationInitiator`/`KtorProvider`.
3. **clientId стабилен**: источник — `launcher.ini` (`application.clientId`), по нему работают
   статистика и бета-доступ. Не генерировать новый при запуске.
4. **Прерывание загрузки ≠ чистый стоп**: v1-TCP пишет прямо в файл, обрыв оставляет частичный
   файл — это норма, следующий проход лечит по контрольным суммам (MD5 v1 / SHA-256 v2).
5. **v2 включается сам**: проба `/v2/game/channels` на старте; ответ есть → v2-жизненный цикл
   и задачи, нет → v1-путь. Не переключать руками.
6. **Скин — пиксель-в-паритет.** Геометрия из CSS оригинала (строки `style.css`), арты без
   растяжения там, где оригинал рисует 1:1. Отступы/размеры меняем только с эталоном рядом.

## Сборка и проверка

```
dotnet test Mfr.Protocol.Tests -c Release        # контракты (обязательно зелёные)
dotnet build Mfr.Launcher -c Release
powershell -File publish.ps1                     # тесты + single-file exe → publish/
```

CI: GitHub Actions (`.github/workflows/build.yml`) — тесты + артефакт + релиз по тегу `v*`.
Версия продукта — `<Version>` в `Mfr.Launcher/Mfr.Launcher.csproj`; тег равен версии.

## Грабли (выстрадано)

- **Avalonia**: `Classes` через пробел, не запятую; `ControlTheme` живёт в `Styles.Resources`;
  в шаблонах `ContentPresenter` требует явный `Content="{TemplateBinding Content}"`; отражательные
  привязки отключены (`AvaloniaUseCompiledBindingsByDefault=false`) — в single-file они умирают;
  `TemplateBinding` поддерживает `Mode=TwoWay`; ComboBox-шаблон НЕ содержит ToggleButton — клики
  ловит сам контрол, попап открывает только `IsOpen="{TemplateBinding IsDropDownOpen}"`.
- **Потоки**: задачи бегут в воркерах — любое окно создаётся через `Dispatcher.UIThread.InvokeAsync`;
  диалоги экрана настроек открываются от ВИДИМОГО окна (главное в этот момент скрыто — иначе крах).
- **Прогресс**: события из воркеров → `Dispatcher.Post`, троттлинг ~10/с, иначе клики умирают.
- **Пауза**: продакшен рвёт приостановленные TCP-каналы; пауза = отмена задачи, продолжение =
  перезапуск с пропуском по контрольным суммам.
- **Пути с `[FR]`** в шелле/gh — glob-класс; всегда в кавычках или переименовать.

## Диагностика

- `task-trace.log` (рядом с exe, ротация 1 МБ) — статусы, старт/финиш задач, решения v2-активации.
- `crash.log` — необработанные исключения.
- `launcher.json` рядом с exe переопределяет адреса серверов (см. `launcher.example.json`).
- Миграция: первый запуск над старым клиентом импортирует PROPERTY из `launcher.mv.db` (H2)
  и пересобирает секции опций по файлам (FillSchema).
