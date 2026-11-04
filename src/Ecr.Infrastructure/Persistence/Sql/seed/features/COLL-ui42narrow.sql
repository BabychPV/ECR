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
-- COLL:ui42narrow ── UI-42: документ лише для читання на вузькому екрані (банер docs-narrow-note); ru/kz — порцією COLL:ui42narrow у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.narrow.title', N'en', N'Read-only on a narrow screen', 1),
    (N'document.narrow.text', N'en', N'Values can be viewed here but not changed. To enter or correct data, open this document on a wider screen.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui42narrow ── кінець секції ──
