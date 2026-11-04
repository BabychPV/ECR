-- HSE301:U2 ── «де використовується одиниця» бачить прив'язки PI за вікном рядка
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- (ext.RowWindowMap / ext.RowWindowSource): без виду видалення падало на FK голим 500.
    (N'usageKind.rowWindowMap', N'en', N'PI row-window binding', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:U2 ── кінець секції
