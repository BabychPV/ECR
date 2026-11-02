-- REG-UI:actions ── ru/kz UI довідників, коміт 3: рядок дій сторінки записів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.moreActions', N'ru', N'Ещё'),
    (N'registries.moreActions', N'kz', N'Тағы')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG-UI:actions ── кінець секції ──
