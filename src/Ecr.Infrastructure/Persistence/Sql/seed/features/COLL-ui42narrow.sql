-- COLL:ui42narrow ── ru/kz UI-42: документ лише для читання на вузькому екрані (банер docs-narrow-note); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.narrow.title', N'ru', N'Только просмотр на узком экране'),
    (N'document.narrow.title', N'kz', N'Тар экранда тек қарау'),
    (N'document.narrow.text', N'ru', N'Здесь значения можно просматривать, но не изменять. Чтобы ввести или исправить данные, откройте документ на более широком экране.'),
    (N'document.narrow.text', N'kz', N'Мұнда мәндерді қарауға болады, бірақ өзгертуге болмайды. Деректерді енгізу немесе түзету үшін құжатты кеңірек экранда ашыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui42narrow ── кінець секції ──
