-- COLL:ui33periods ── ru/kz UI-33: Periods — плитки року й огляд проєктів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'periods.tile.closesToday', N'ru', N'закрывается сегодня'),
    (N'periods.tile.closesIn.one', N'ru', N'закрывается через {count} день'),
    (N'periods.tile.closesIn.few', N'ru', N'закрывается через {count} дня'),
    (N'periods.tile.closesIn.many', N'ru', N'закрывается через {count} дней'),
    (N'periods.tile.closesIn.other', N'ru', N'закрывается через {count} дня'),
    (N'periods.tile.reopened', N'ru', N'переоткрыт до {date} · правки поздние'),
    (N'periods.tile.grace', N'ru', N'льготный период до {date} · правки поздние'),
    (N'periods.tile.opens', N'ru', N'откроется {date}'),
    (N'periods.tile.readOnly', N'ru', N'только чтение'),
    (N'periods.tile.now', N'ru', N'сейчас'),
    (N'periods.year.title', N'ru', N'{project} · {year}'),
    (N'periods.tile.closesToday', N'kz', N'бүгін жабылады'),
    (N'periods.tile.closesIn.one', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.other', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.few', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.many', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.reopened', N'kz', N'{date} дейін қайта ашылды · түзетулер кешіккен'),
    (N'periods.tile.grace', N'kz', N'жеңілдік кезеңі {date} дейін · түзетулер кешіккен'),
    (N'periods.tile.opens', N'kz', N'{date} ашылады'),
    (N'periods.tile.readOnly', N'kz', N'тек оқу'),
    (N'periods.tile.now', N'kz', N'қазір'),
    (N'periods.year.title', N'kz', N'{project} · {year}'),
    -- Pass2 п. 6 (людський прохід 07.11): плитки неактивного проєкту не обіцяють дат; смуга названа «всі проєкти».
    (N'periods.tile.notOpenDraft', N'ru', N'не открыт — проект черновик'),
    (N'periods.tile.notOpenDraft', N'kz', N'ашылмаған — жоба нобай күйінде'),
    (N'periods.tile.notOpenInactive', N'ru', N'не открыт — проект неактивен'),
    (N'periods.tile.notOpenInactive', N'kz', N'ашылмаған — жоба белсенді емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui33periods ── кінець секції ──
-- COLL:ui33periods ── UI-33: Periods — плитки періодів року, «All projects · current period», смуга показників; ru/kz — порцією COLL:ui33periods у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'periods.tile.closesToday', N'en', N'closes today', 1),
    (N'periods.tile.closesIn.one', N'en', N'closes in {count} day', 1),
    (N'periods.tile.closesIn.other', N'en', N'closes in {count} days', 1),
    (N'periods.tile.closesIn.few', N'en', N'closes in {count} days', 1),
    (N'periods.tile.closesIn.many', N'en', N'closes in {count} days', 1),
    (N'periods.tile.reopened', N'en', N'reopened until {date} · edits are late', 1),
    (N'periods.tile.grace', N'en', N'grace period until {date} · edits are late', 1),
    (N'periods.tile.opens', N'en', N'opens {date}', 1),
    (N'periods.tile.readOnly', N'en', N'read-only', 1),
    (N'periods.tile.now', N'en', N'now', 1),
    (N'periods.year.title', N'en', N'{project} · {year}', 1),
    -- Pass2 п. 6 (людський прохід 07.11): плитки неактивного проєкту не обіцяють дат; смуга названа «всі проєкти».
    (N'periods.tile.notOpenDraft', N'en', N'not open — the project is a draft', 1),
    (N'periods.tile.notOpenInactive', N'en', N'not open — the project is not active', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui33periods ── кінець секції ──
