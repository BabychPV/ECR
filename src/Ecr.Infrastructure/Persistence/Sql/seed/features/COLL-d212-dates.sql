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

-- COLL:d212-dates ── Вікно дії запису з дат AF (D-212 PR-7, `RegistrySyncValidity`, PUT /sources/{id}/registry/policy) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.validityDateInvalid',          N'en', N'The source value is not an ISO 8601 date: the validity window of the entry is left unchanged.', 1),
    (N'err.ECR-REG-0422.validityWindowEmptyInSource',  N'en', N'The source dates give an empty validity window (the end is not later than the start): the window is left unchanged.', 1),
    (N'err.ECR-REQ-0422.registrySyncPolicyNotTemporal', N'en', N'Registry {registry} is not temporal: validity date attributes cannot be synced into it.', 1)
    -- ru/kz — окремою порцією `COLL:d212-dates` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d212-dates ── кінець секції ──
