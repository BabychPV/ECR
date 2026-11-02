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

