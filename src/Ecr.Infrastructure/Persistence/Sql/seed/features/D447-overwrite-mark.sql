-- D447:overwrite-mark ── ru/kz: позначка журналу «перезаписав чужу правку» (D-447); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.overwriteMark', N'ru', N'перезаписал чужую правку'),
    (N'audit.overwriteMark', N'kz', N'басқаның түзетуін қайта жазды'),
    (N'audit.overwriteHint', N'ru', N'Эта правка сознательно перезаписала строку, которую изменил кто-то другой после выгрузки книги.'),
    (N'audit.overwriteHint', N'kz', N'Бұл түзету кітап экспортталғаннан кейін басқа біреу өзгерткен жолды саналы түрде қайта жазды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D447:overwrite-mark ── кінець секції ──
-- D447:overwrite-mark ── en: позначка журналу «перезаписав чужу правку» поруч із «правкою погоджувача» (D-447); ru/kz — порцією D447:overwrite-mark нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.overwriteMark', N'en', N'overwrote another''s edit', 1),
    (N'audit.overwriteHint', N'en', N'This edit knowingly overwrote a row that someone else changed after the workbook was exported.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D447:overwrite-mark ── кінець секції ──
