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
-- CL:states-a11y-2 ── поле Lookup без цілі у вкладці «Зв'язки» (стани нових панелей 2) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.relationTargetMissing', N'en', N'Target not set: choose a registry', 1)
    -- ru/kz — окремою порцією CL:states-a11y-2 у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CL:states-a11y-2 ── кінець секції ──
