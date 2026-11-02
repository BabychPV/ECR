-- COLL:a5len ── ru/kz RC5: текст довший за стовпець; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.column.maxLength', N'ru', N'Текст в колонке «{column}» длиннее {maxLength} символов ({actualLength}).'),
    (N'validation.column.maxLength', N'kz', N'«{column}» бағанындағы мәтін {maxLength} таңбадан ұзын ({actualLength}).'),
    (N'err.ECR-CELL-0422.importValueTooLong', N'ru', N'Текст длиннее, чем допускает колонка (1000 символов).'),
    (N'err.ECR-CELL-0422.importValueTooLong', N'kz', N'Мәтін баған рұқсат ететіннен ұзын (1000 таңба).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a5len ── кінець секції ──
-- COLL:a5len ── RC5: текст довший за стовпець (1000 символів) — людська відмова в комірці й у перегляді імпорту; ru/kz — порцією COLL:a5len у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'validation.column.maxLength', N'en', N'The text in column "{column}" is longer than {maxLength} characters ({actualLength}).', 1),
    (N'err.ECR-CELL-0422.importValueTooLong', N'en', N'The text is longer than the column allows (1000 characters).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a5len ── кінець секції ──
