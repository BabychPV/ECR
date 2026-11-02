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

