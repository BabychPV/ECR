-- MS:methodology-script ── (методології як скрипт: ключі домену версії; решта ключів MS додається в цей самий файл у наступних задачах) ru/kz: власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.scriptEmpty', N'ru', N'Версию {version} нельзя переключить на скрипт: текста скрипта нет.'),
    (N'err.ECR-CALC-0422.scriptEmpty', N'kz', N'{version} нұсқасын скриптке ауыстыруға болмайды: скрипт мәтіні жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MS:methodology-script ── кінець секції ──

-- MS:methodology-script ── en-порція ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.scriptEmpty', N'en', N'Version {version} cannot be switched to a script: there is no script text.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MS:methodology-script ── кінець секції ──
