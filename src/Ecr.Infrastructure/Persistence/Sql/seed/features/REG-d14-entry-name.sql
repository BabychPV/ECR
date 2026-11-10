-- REG:d14-entry-name ── ECR-REG-4222 ── ru/kz: запис довідника без назви (D14 / AU-14); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-4222', N'ru', N'Нужно название записи'),
    (N'err.ECR-REG-4222', N'kz', N'Жазбаның атауы қажет'),
    (N'err.ECR-REG-4222.entryNameRequired', N'ru', N'Укажите название записи хотя бы на одном языке.'),
    (N'err.ECR-REG-4222.entryNameRequired', N'kz', N'Жазбаның атауын кемінде бір тілде көрсетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG:d14-entry-name ── кінець секції ──
-- REG:d14-entry-name ── ECR-REG-4222 ── en: запис довідника без назви; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-4222', N'en', N'The entry needs a name', 1),
    (N'err.ECR-REG-4222.entryNameRequired', N'en', N'Enter the entry name in at least one language.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG:d14-entry-name ── кінець секції ──
