-- COLL:dates ── ru/kz відмови формату дати поля довідника з AF; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.dateFormatRefused', N'ru', N'Значение источника «{value}» — не дата в ожидаемом формате ({expected}) или ISO 8601: поле не изменено.'),
    (N'err.ECR-REG-0422.dateFormatRefused', N'kz', N'Дереккөз мәні «{value}» — күтілетін форматтағы ({expected}) немесе ISO 8601 күні емес: өріс өзгертілмеді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:dates ── кінець секції ──
