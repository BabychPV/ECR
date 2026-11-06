-- COLL:r11l6-target-hidden ── ru, kz: AN-94: ціль зв'язку недоступна читачеві; власна порція (kz — машинний переклад, AU-13) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.relationTargetHidden', N'ru', N'Справочник-цель вам недоступен'),
    (N'registries.relationTargetHidden', N'kz', N'Мақсатты анықтамалық сізге қолжетімсіз')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l6-target-hidden ── кінець секції ──
-- COLL:r11l6-target-hidden ── AN-94: ціль зв'язку — довідник, до якого в читача немає доступу (сервер віддає null); ru — порцією нижче, kz — за термінологом ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.relationTargetHidden', N'en', N'Target registry is not available to you', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r11l6-target-hidden ── кінець секції ──
