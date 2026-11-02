-- COLL:t4-p3 ── ru/kz тестувального проходу №4 (T4-05); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'profile.languageUnsavedBlocked', N'ru', N'Язык не изменён: часть изменений на этой странице не сохранена. Сохраните или отмените их, затем смените язык.'),
    (N'profile.languageUnsavedBlocked', N'kz', N'Тіл өзгертілмеді: осы беттегі кейбір өзгерістер сақталмаған. Оларды сақтаңыз немесе болдырмаңыз, содан кейін тілді ауыстырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:t4-p3 ── кінець секції ──
