-- RPTSTABLE:type-family ── ru/kz: R-3 (D-478), заборона змінювати родину типу коду колонки між версіями; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'ru', N'Столбец «{columnPath}»: в опубликованных версиях шаблона — {publishedTypes}, здесь — {newType}. Другой тип хранения сломал бы стабильное представление отчётов SSRS: оставьте тип или добавьте столбец с новым кодом.'),
    (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'kz', N'«{columnPath}» бағаны: үлгінің жарияланған нұсқаларында — {publishedTypes}, мұнда — {newType}. Сақтаудың басқа түрі SSRS тұрақты есеп көрінісін бұзар еді: түрін сақтаңыз немесе жаңа кодпен баған қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RPTSTABLE:type-family ── кінець секції ──
-- RPTSTABLE:type-family ── R-3 (D-478): заборона змінювати родину типу коду колонки між версіями; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.columnTypeFamilyChanged', N'en', N'Column "{columnPath}": {publishedTypes} in the published versions of this template, {newType} here. A different storage type would break the stable SSRS report view: keep the type or add a column with a new code.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RPTSTABLE:type-family ── кінець секції ──
