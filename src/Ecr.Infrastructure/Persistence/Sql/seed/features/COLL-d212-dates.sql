-- COLL:d212-dates ── ru/kz вікна дії запису з дат AF (D-212 PR-7); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.validityDateInvalid', N'ru', N'Значение источника — не дата ISO 8601: период действия записи не изменён.'),
    (N'err.ECR-REG-0422.validityDateInvalid', N'kz', N'Дереккөз мәні ISO 8601 күні емес: жазбаның әрекет ету кезеңі өзгертілмеді.'),
    (N'err.ECR-REG-0422.validityWindowEmptyInSource', N'ru', N'Даты источника дают пустой период действия (конец не позже начала): период не изменён.'),
    (N'err.ECR-REG-0422.validityWindowEmptyInSource', N'kz', N'Дереккөз күндері бос әрекет ету кезеңін береді (соңы басынан кейін емес): кезең өзгертілмеді.'),
    (N'err.ECR-REQ-0422.registrySyncPolicyNotTemporal', N'ru', N'Справочник {registry} не темпоральный: атрибуты дат действия в него не синхронизируются.'),
    (N'err.ECR-REQ-0422.registrySyncPolicyNotTemporal', N'kz', N'{registry} анықтамалығы темпоральды емес: әрекет ету күндерінің атрибуттары оған синхрондалмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d212-dates ── кінець секції ──

