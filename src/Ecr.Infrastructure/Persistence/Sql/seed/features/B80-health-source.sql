-- B80:health-source ── ru/kz B80 N6: ворота зв'язки HSE400 → Land на сторінці «Здоров'я системи»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.facts.hse400Source', N'ru', N'Land: допуск источника HSE400'),
    (N'health.facts.hse400Source', N'kz', N'Land: HSE400 көзіне рұқсат'),
    (N'health.facts.hse400Source.any', N'ru', N'Все введённые данные (утверждение не требуется)'),
    (N'health.facts.hse400Source.any', N'kz', N'Барлық енгізілген деректер (бекіту қажет емес)'),
    (N'health.facts.hse400Source.approvedOnly', N'ru', N'Только утверждённые месяцы'),
    (N'health.facts.hse400Source.approvedOnly', N'kz', N'Тек бекітілген айлар'),
    (N'health.facts.hse400Source.approvedMonths', N'ru', N'утверждено месяцев: {count} из 12'),
    (N'health.facts.hse400Source.approvedMonths', N'kz', N'бекітілген айлар: 12-ден {count}'),
    (N'health.hse400SourceWarning.title', N'ru', N'Не все месяцы источника HSE400 для Land утверждены'),
    (N'health.hse400SourceWarning.title', N'kz', N'Land үшін HSE400 көзінің барлық айлары бекітілмеген'),
    (N'health.hse400SourceWarning.textAny', N'ru', N'Связь HSE400 → Land ещё читает все введённые данные, включая неутверждённые месяцы. Утвердите месяцы HSE400, затем переключите допуск на «Только утверждённые месяцы».'),
    (N'health.hse400SourceWarning.textAny', N'kz', N'HSE400 → Land байланысы әлі де барлық енгізілген деректерді, соның ішінде бекітілмеген айларды оқиды. HSE400 айларын бекітіп, рұқсатты «Тек бекітілген айлар» күйіне ауыстырыңыз.'),
    (N'health.hse400SourceWarning.textApprovedOnly', N'ru', N'Месяц с введёнными данными HSE400 не утверждён: Land за этот месяц считается без состава газа. Утвердите месяц в документе HSE400.'),
    (N'health.hse400SourceWarning.textApprovedOnly', N'kz', N'HSE400 деректері енгізілген ай бекітілмеген: Land бұл ай үшін газ құрамынсыз есептеледі. HSE400 құжатында айды бекітіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:health-source ── кінець секції ──
-- B80:health-source ── B80 N6: ворота HSE400 → Land у фактах і попередження; ru/kz — порцією B80:health-source вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.facts.hse400Source', N'en', N'Land: HSE400 source gate', 1),
    (N'health.facts.hse400Source.any', N'en', N'All entered data (approval not required)', 1),
    (N'health.facts.hse400Source.approvedOnly', N'en', N'Approved months only', 1),
    (N'health.facts.hse400Source.approvedMonths', N'en', N'{count} of 12 months approved', 1),
    (N'health.hse400SourceWarning.title', N'en', N'Not every month of the HSE400 source for Land is approved', 1),
    (N'health.hse400SourceWarning.textAny', N'en', N'The HSE400 → Land link still reads all entered data, including months that are not approved. Approve the HSE400 months, then switch the gate to «Approved months only».', 1),
    (N'health.hse400SourceWarning.textApprovedOnly', N'en', N'A month with entered HSE400 data is not approved, so Land calculates that month without a gas composition. Approve the month in the HSE400 document.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:health-source ── кінець секції ──
