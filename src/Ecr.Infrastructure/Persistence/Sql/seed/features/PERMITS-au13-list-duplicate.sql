-- PERMITS: au13-list-duplicate ── ru/kz: AU-13 N3, повтор значення списку — назва списку і повторене значення (kz — машинний переклад) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.lists.duplicateValue', N'ru', N'В списке «{list}» уже есть значение «{value}».'),
    (N'permits.lists.duplicateValue', N'kz', N'«{list}» тізімінде «{value}» мәні бұрыннан бар.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: au13-list-duplicate ── кінець секції ──
-- PERMITS: au13-list-duplicate ── en: AU-13 N3, повтор значення списку; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.lists.duplicateValue', N'en', N'The list «{list}» already has the value «{value}».', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: au13-list-duplicate ── кінець секції ──
