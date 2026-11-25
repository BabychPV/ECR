# Картка: server-language-rest Решта українських текстів сервера, що доходять до UI
Статус: planned
Гілка: lane/integ-server-language-rest
Пачка: b14
Ризик: R2
Рішення: людина 25.11 «не забувай про мову чому помилки Українською»
Дизайн: не потрібен

## 2. Критерії приймання
| AC | Що спостерігаємо | Доказ | SHA |
|----|----|----|----|
| AC-1 | MethodologyVersion RequireDraft/RequireOwn (~35) — код частини + ключі | тест + сторож | |
| AC-2 | ChangeReason аудиту (імпорт методології, перміти, клон проєкту, довідники, FLERT) — ключі | тест | |
| AC-3 | помилки доставки (Notification/Outbox/Teams/Smtp) — ключі | тест | |
| AC-4 | помилки задач, PeriodStateCalculator.skipped, FlertComposition*, MethodologyRegistryChecks, ExceptionHandlingMiddleware (530, 757–805) | тест | |
| AC-5 | адаптери ExcelImporter, ImportDiffBuilder, PiAf/Sql тексти | тест | |
| AC-6 | сторож UserFacingMessageLanguageTests розширено на ці місця | arch | |

Перелік файлів:рядків — звіт агента required-banner 25.11 (TRACKING).
