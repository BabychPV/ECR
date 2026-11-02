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
