-- COLL:fv812-ifmatch ── ru/kz обов'язкового If-Match на PUT опису довідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.definitionVersionRequired', N'ru', N'Для сохранения определения справочника нужен заголовок If-Match с версией определения, которую вы читали. Перезагрузите определение и сохраните снова.'),
    (N'err.ECR-REQ-0422.definitionVersionRequired', N'kz', N'Анықтамалық анықтамасын сақтау үшін Сіз оқыған анықтама нұсқасы бар If-Match тақырыбы қажет. Анықтаманы қайта жүктеп, қайта сақтаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:fv812-ifmatch ── кінець секції ──

-- COLL:fv812-ifmatch ── обов'язковий If-Match на PUT опису довідника ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.definitionVersionRequired', N'en', N'Saving a registry definition needs an If-Match header carrying the definition version you read. Reload the definition and save again.', 1)
    -- ru/kz — окремою порцією COLL:fv812-ifmatch у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:fv812-ifmatch ── кінець секції ──
