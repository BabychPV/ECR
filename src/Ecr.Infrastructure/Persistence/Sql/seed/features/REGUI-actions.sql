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
-- REG-UI:actions ── UI довідників, коміт 3: рядок дій сторінки записів (головна + 2 другорядні + «Ще»); ru/kz — порцією REG-UI:actions у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.moreActions', N'en', N'More', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG-UI:actions ── кінець секції ──
