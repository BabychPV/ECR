-- I18N:keys-audit 2026-10-01 ── форми множини ru (few/many) для formatCount; власна порція ──
-- kz за `Intl.PluralRules` має лише one/other — рядки kz тут лише для повноти
-- каталогу (текст = other). ⚠ kz — нейтральний переклад, потребує перевірки носієм.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.testEntities.few', N'ru', N'В каталоге источника {count} сущности.'),
    (N'sources.testEntities.few', N'kz', N'Көз каталогында {count} нысан бар.'),
    (N'sources.testEntities.many', N'ru', N'В каталоге источника {count} сущностей.'),
    (N'sources.testEntities.many', N'kz', N'Көз каталогында {count} нысан бар.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- I18N:keys-audit ── кінець секції ──

