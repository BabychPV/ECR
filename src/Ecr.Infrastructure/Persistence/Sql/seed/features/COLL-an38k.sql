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
