-- COLL:an38k── ru/kz L4-06/L5-03: первинний ключ на записах без значення частини; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.primaryKeyEmptyParts', N'ru', N'Первичный ключ {key} нельзя включить: у {entries} записей нет значения части ключа. Сначала заполните его или удалите эти записи.'),
    (N'err.ECR-REG-0422.primaryKeyEmptyParts', N'kz', N'Негізгі кілт {key} қосу мүмкін емес: {entries} жазбада кілт бөлігінің мәні жоқ. Алдымен оны толтырыңыз немесе осы жазбаларды жойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an38k ── кінець секції ──
-- COLL:an38k ── L4-06/L5-03: первинний ключ на записах без значення частини; ru/kz — порцією COLL:an38k у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.primaryKeyEmptyParts', N'en', N'Primary key {key} cannot be enabled: {entries} entries have no value in a part of the key. Fill it in or delete those entries first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an38k ── кінець секції ──
