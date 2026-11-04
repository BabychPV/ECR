-- COLL:migrate-rename-map ── ru/kz: перенос версии, карта «было → стало» кодов таблиц/колонок; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateRenameConflict', N'ru', N'Карта переименований ведёт в таблицу или колонку, у которой уже есть соответствие по коду в переносимой версии, либо две записи ведут в одну и ту же. Ничего не перенесено, значения не сливаются в одно место. Исправьте карту (или целевую версию) и повторите; отчёт перечисляет каждую конфликтную пару.'),
    (N'err.ECR-SCHM-0422.migrateRenameConflict', N'kz', N'Атауды өзгерту картасы көшірілетін нұсқада коды бойынша сәйкестігі бар кестеге немесе бағанға апарады, не екі жазба бір кестеге апарады. Ештеңе көшірілмеді, мәндер бір жерге біріктірілмейді. Картаны (немесе мақсатты нұсқаны) түзетіп, қайталаңыз; есеп әрбір қайшы жұпты көрсетеді.'),
    (N'err.ECR-SCHM-0422.migrateRenameMapInvalid', N'ru', N'Карта переименований таблиц составлена неверно: коды должны быть непустыми (до 64 символов), исходная таблица указывается один раз, допускается не более {maxTableRenames} таблиц и не более {maxColumnRenames} переименований колонок в каждой.'),
    (N'err.ECR-SCHM-0422.migrateRenameMapInvalid', N'kz', N'Кестелердің атын өзгерту картасы дұрыс құрастырылмаған: кодтар бос болмауы керек (64 таңбаға дейін), бастапқы кесте бір рет көрсетіледі, ең көбі {maxTableRenames} кесте және әрқайсысында ең көбі {maxColumnRenames} бағанның атын өзгертуге болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:migrate-rename-map ── кінець секції ──
-- COLL:migrate-rename-map ── перенос версії: мапа «було → стало» кодів таблиць/колонок; ru/kz — порцією COLL:migrate-rename-map нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateRenameConflict', N'en', N'The rename map leads into a table or column that already has a counterpart by code in the version being moved from, or two entries lead into the same one. Nothing was moved, so values are not merged into one place. Fix the map (or the target version) and retry; the report lists each conflicting pair.', 1),
    (N'err.ECR-SCHM-0422.migrateRenameMapInvalid', N'en', N'The table rename map is malformed: codes must be non-empty (up to 64 characters), a source table may be listed once, and at most {maxTableRenames} tables with at most {maxColumnRenames} column renames each are allowed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:migrate-rename-map ── кінець секції ──
