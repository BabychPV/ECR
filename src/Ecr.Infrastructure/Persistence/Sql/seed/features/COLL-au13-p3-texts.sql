-- COLL:au13-p3-texts ── ru/kz: AU-13 P3-2/P3-3 — власні тексти відмов імпорту таблиці дозволу (kz — машинний переклад) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.lookupValueTooLong', N'ru', N'Значение длиннее {max} символов. Сократите его.'),
    (N'err.ECR-REQ-0422.lookupValueTooLong', N'kz', N'Мән {max} таңбадан ұзын. Оны қысқартыңыз.'),
    (N'err.ECR-IMP-0422.permitImportSheetMissing', N'ru', N'В книге нет листа «{sheet}». Выберите один из листов файла.'),
    (N'err.ECR-IMP-0422.permitImportSheetMissing', N'kz', N'Кітапта «{sheet}» парағы жоқ. Файлдағы парақтардың бірін таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:au13-p3-texts ── кінець секції ──
-- COLL:au13-p3-texts ── AU-13 P3-2: задовге значення в import/lookups; P3-3: обраного аркуша немає в книзі дозволу ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.lookupValueTooLong', N'en', N'A value is longer than {max} characters. Shorten it.', 1),
    (N'err.ECR-IMP-0422.permitImportSheetMissing', N'en', N'The workbook has no sheet «{sheet}». Choose one of the sheets of the file.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:au13-p3-texts ── кінець секції ──
