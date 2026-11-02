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
-- COLL:an34m ── AN-34 L4-01: довідник тримає одна сутність збору на з'єднання; ru/kz — порцією COLL:an34m у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-INT-0409.registryAlreadyBound', N'en', N'Registry {registryDefId} is already bound to another collection entity of this connection. A connection takes one entity per registry: unbind the other entity first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an34m ── кінець секції ──
