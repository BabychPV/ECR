-- COLL:ui-version-migration-range ── ru/kz: діалог переносу версії «увесь проєкт / з місяця» (RPT-STABLE Part B, підказка і ефективний період); власна порція; en — у MERGE 00-prelude.sql ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.migrateFromMonthHint', N'ru', N'Более ранние месяцы остаются на старой версии; этот месяц и следующие переходят на новую.'),
    (N'documents.migrateFromMonthHint', N'kz', N'Бұрынғы айлар ескі нұсқада қалады; осы және кейінгі айлар жаңасына өтеді.'),
    (N'documents.migrateEffectiveFrom', N'ru', N'Перенос действует с периода {period}.'),
    (N'documents.migrateEffectiveFrom', N'kz', N'Көшіру {period} кезеңінен бастап күшіне енеді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-version-migration-range ── кінець секції ──
