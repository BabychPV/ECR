-- NAV:cell-click ── ru/kz переходу від зауваження перевірки до комірки (ФВ-5.6); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.validationGoTo', N'ru', N'Показать в таблице'),
    (N'document.validationGoTo', N'kz', N'Кестеде көрсету')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAV:cell-click ── кінець секції ──

-- NAV:cell-click ── ФВ-5.6: перехід від зауваження перевірки до комірки (`ValidationPanel`).
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.validationGoTo', N'en', N'Show in the table', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAV:cell-click: кінець секції
