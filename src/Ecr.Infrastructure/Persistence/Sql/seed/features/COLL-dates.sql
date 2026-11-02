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
-- COLL:dates ── Дата-рядок поля довідника з AF: лише MM/dd/yyyy чи ISO 8601 (`RegistrySyncValidity.TryParseFieldDate`, відповідь людини 2026-10-01) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.dateFormatRefused', N'en', N'The source value "{value}" is not a date in the expected format ({expected}) or ISO 8601: the field is left unchanged.', 1)
    -- ru/kz — окремою порцією `COLL:dates` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:dates ── кінець секції ──
