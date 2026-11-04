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
-- COLL:t4-p3 ── тестувальний прохід №4, T4-05: мову не змінено через незбережений ввід; ru/kz — порцією COLL:t4-p3 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'profile.languageUnsavedBlocked', N'en', N'The language was not changed: some changes on this page are not saved. Save or undo them, then change the language.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:t4-p3 ── кінець секції ──
