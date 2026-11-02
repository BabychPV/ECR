-- COLL:an34m ── ru/kz AN-34 L4-01: довідник уже прив'язаний до іншої сутності збору з'єднання; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-INT-0409.registryAlreadyBound', N'ru', N'Справочник {registryDefId} уже привязан к другой сущности сбора этого подключения. Подключение держит одну сущность на справочник: сначала отвяжите другую сущность.'),
    (N'err.ECR-INT-0409.registryAlreadyBound', N'kz', N'{registryDefId} анықтамалығы осы қосылымның жинау нысандарының біріне бұрыннан байланған. Қосылым бір анықтамалыққа бір нысанды ұстайды: алдымен басқа нысанды ажыратыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an34m ── кінець секції ──
