-- NAEUX:wizard-hints ── змінений текст наявного ключа: «New source rows» казав «нові», хоча це всі рядки з файла (нові бази беруть нове значення з COLL-ui-air-import.sql; епілог оновлює наявні, лише поки там СТАРЕ значення) ──
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.air.import.replace.new', N'en', N'New source rows', N'Rows from the file'),
    (N'permits.air.import.replace.new', N'ru', N'Новые строки источников', N'Строки из файла'),
    (N'permits.air.import.replace.new', N'kz', N'Көздердің жаңа жолдары', N'Файлдағы жолдар')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- NAEUX:wizard-hints ── кінець секції ──
