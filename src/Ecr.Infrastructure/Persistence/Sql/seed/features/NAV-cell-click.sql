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

