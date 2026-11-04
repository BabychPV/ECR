-- COLL:an13-strict ── ru/kz відмови публікації Strict заднім числом (ФВ-9.9, `PublishMethodologyHandler`); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.strictBackdated', N'ru', N'Режим Strict включается только явным решением с новой даты вступления в силу, не задним числом: дата {effectiveFrom} не позже сегодняшней ({today}). Укажите будущую дату.'),
    (N'err.ECR-CALC-0422.strictBackdated', N'kz', N'Strict режимі тек айқын шешіммен, күшіне енудің жаңа күнінен бастап қосылады, бұрынғы күнмен емес: {effectiveFrom} күні бүгінгіден ({today}) кеш емес. Болашақ күнді көрсетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an13-strict ── кінець секції ──

-- COLL:an13-strict ── Відмова публікації методології: Strict заднім числом (ФВ-9.9, `PublishMethodologyHandler`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.strictBackdated',         N'en', N'Strict mode is switched on only by an explicit decision from a new effective date, never retroactively: {effectiveFrom} is not later than today ({today}). Choose a future date.', 1)
    -- ru/kz — окремою порцією `COLL:an13-strict` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an13-strict ── кінець секції ──
