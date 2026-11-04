-- COLL:r7y5-07-registry-changes ── ru/kz: R7-Y5 / Y5-07: перевірка CSV довідника показує старе й нове значення кожного зміненого поля наявних записів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registry.import.changesTitle', N'ru', N'Изменения в существующих записях ({count}). Excel может менять значения при сохранении CSV (ведущие нули, длинные числа, даты), поэтому проверьте их перед применением.'),
    (N'registry.import.changesTitle', N'kz', N'Бар жазбалардағы өзгерістер ({count}). Excel CSV-ді сақтағанда мәндерді өзгертуі мүмкін (алдыңғы нөлдер, ұзын сандар, күндер), сондықтан қолданар алдында тексеріңіз.'),
    (N'registry.import.changesTruncated', N'ru', N'Показаны только первые {count} изменений.'),
    (N'registry.import.changesTruncated', N'kz', N'Тек алғашқы {count} өзгеріс көрсетілген.'),
    (N'registry.import.oldValue', N'ru', N'Текущее значение'),
    (N'registry.import.oldValue', N'kz', N'Ағымдағы мән'),
    (N'registry.import.newValue', N'ru', N'Значение из файла'),
    (N'registry.import.newValue', N'kz', N'Файлдағы мән')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-07-registry-changes ── кінець секції ──

-- COLL:r7y5-07-registry-changes ── R7-Y5 / Y5-07: перевірка CSV довідника показує старе й нове значення кожного зміненого поля наявних записів; ru/kz — порцією COLL:r7y5-07-registry-changes нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registry.import.changesTitle', N'en', N'Changes in existing entries ({count}). Excel may change values when saving a CSV (leading zeros, long numbers, dates), so check them before applying.', 1),
    (N'registry.import.changesTruncated', N'en', N'Only the first {count} changes are shown.', 1),
    (N'registry.import.oldValue', N'en', N'Current value', 1),
    (N'registry.import.newValue', N'en', N'Value from file', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-07-registry-changes ── кінець секції ──
