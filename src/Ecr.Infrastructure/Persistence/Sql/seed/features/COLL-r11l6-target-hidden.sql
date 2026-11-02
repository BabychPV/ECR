-- COLL:r11l6-target-hidden ── ru: AN-94: ціль зв'язку недоступна читачеві; власна порція (kz — за термінологом) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.relationTargetHidden', N'ru', N'Справочник-цель вам недоступен')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l6-target-hidden ── кінець секції ──
