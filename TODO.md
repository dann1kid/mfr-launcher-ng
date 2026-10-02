# TODO: недостающее до 1-в-1 со старым лончером

## Функциональные
- [x] Окно «Пресеты MGE» (кнопка MGE): Мощный/Средний/Слабый/Древний компьютер/Свои настройки + «Старт MGE»
- [x] Окно «Благодарность» (Помочь проекту): текст + карта Сбербанка + контакты автора
- [x] Тексты пресетов OpenMW как в оригинале («Мощный компьютер» и т.д.)
- [ ] Readme: локальный Manual\readme\index.html при выключенном Online-mod, иначе сайт
- [ ] Splash-заставка при старте (start.fxml: сердце + полоса) — в оригинале на время загрузки

## Косметика скина (CSS оригинала → Avalonia)
- [ ] Радиокнопки: element/radio-button-enable.png, radio-button-disable.png
- [ ] Чекбоксы: element/checkbox.png, checkbox-full.png
- [ ] ComboBox на вкладке настроек: element/combo-box.png, combo-box-arrow.png
- [ ] ToggleButton пресетов в окнах MGE/OpenMW: button/s229_brown.png + hover
- [ ] Кнопки s59_blue_file / s59_blue_folder (экран опций)
- [ ] Скроллбары: scrollbars/shadow_red_*.png, shadow_blue_*.png
- [ ] Кнопка Online-mod: button/offline-mode.png / online-mode.png (40×40, переключатель на вкладке настроек)
- [ ] Текстовые тени на красных текстах (dropshadow оранжевый)
- [ ] Вкладка настроек: заголовок settings icon/settings.png вверху

## Фазы плана
- [ ] Фаза 4: Mfr.Updater (wait pid → replace → restart)
- [ ] Фаза 5: поллинг /api/v1/game + /api/v1/client (таймер 5 мин)
- [ ] Фаза 6: self-contained single-file publish + сценарий первой установки (FillScheme+CheckConsistency вместо H2)
