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
