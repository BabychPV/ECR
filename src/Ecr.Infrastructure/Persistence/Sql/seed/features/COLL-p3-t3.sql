-- COLL:p3-t3 ── ru/kz пакета P3 тестувального проходу №3; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0409.relationCodeTaken.title', N'ru', N'Код связи уже используется'),
    (N'err.ECR-TMPL-0409.relationCodeTaken.title', N'kz', N'Байланыс коды бұрыннан қолданылады'),
    (N'grid.tableLoadNow', N'ru', N'Загрузить сейчас'),
    (N'grid.tableLoadNow', N'kz', N'Қазір жүктеу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-t3 ── кінець секції ──
