-- CL:states-a11y-2 ── ru/kz поля Lookup без цілі (стани нових панелей 2); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.relationTargetMissing', N'ru', N'Цель не задана: выберите справочник'),
    (N'registries.relationTargetMissing', N'kz', N'Мақсат берілмеген: анықтамалықты таңдаңыз')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CL:states-a11y-2 ── кінець секції ──
