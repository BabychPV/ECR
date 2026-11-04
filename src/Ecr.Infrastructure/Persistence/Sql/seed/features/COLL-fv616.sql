-- COLL:fv616 ── ru/kz розрізу доступу на аркуші, таблиці й колонці; власна порція #I18N ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.effectiveAccessProject', N'ru', N'Лист, таблица или столбец рассматриваются внутри одного проекта: передайте projectId.'),
    (N'err.ECR-REQ-0422.effectiveAccessProject', N'kz', N'Парақ, кесте немесе баған бір жоба шеңберінде қаралады: projectId беріңіз.'),
    (N'err.ECR-TMPL-0404.effectiveAccessResource', N'ru', N'{resource} не найден.'),
    (N'err.ECR-TMPL-0404.effectiveAccessResource', N'kz', N'{resource} табылмады.'),
    (N'err.ECR-TMPL-0404.effectiveAccessNotInProject', N'ru', N'{resource} не входит в шаблон проекта {projectId}.'),
    (N'err.ECR-TMPL-0404.effectiveAccessNotInProject', N'kz', N'{resource} {projectId} жобасының үлгісіне кірмейді.'),
    (N'effectiveAccess.hintAll', N'ru', N'Выберите справочник, проект либо лист, таблицу или столбец внутри проекта, чтобы увидеть итоговый уровень и какой грант какой роли его даёт. Это лишь объяснение решения, ничего не меняется.'),
    (N'effectiveAccess.hintAll', N'kz', N'Түпкілікті деңгейді және оны қай рөлдің қай гранты беретінін көру үшін анықтамалықты, жобаны немесе жоба ішіндегі парақты, кестені не бағанды таңдаңыз. Бұл тек шешімнің түсіндірмесі, ештеңе өзгермейді.'),
    (N'effectiveAccess.kindSheet', N'ru', N'Лист'),
    (N'effectiveAccess.kindSheet', N'kz', N'Парақ'),
    (N'effectiveAccess.kindTable', N'ru', N'Таблица'),
    (N'effectiveAccess.kindTable', N'kz', N'Кесте'),
    (N'effectiveAccess.kindColumn', N'ru', N'Столбец'),
    (N'effectiveAccess.kindColumn', N'kz', N'Баған'),
    (N'effectiveAccess.projectId', N'ru', N'ID проекта'),
    (N'effectiveAccess.projectId', N'kz', N'Жоба ID'),
    (N'effectiveAccess.caveat', N'ru', N'Состояние документа (подан, утверждён, закрытый период) и сужение назначения по периодам здесь не учитываются. Показано, что дают гранты по цепочке проект, лист, таблица, столбец, а не можно ли редактировать ячейку сейчас.'),
    (N'effectiveAccess.caveat', N'kz', N'Құжат күйі (тапсырылған, бекітілген, жабық кезең) және тағайындаудың кезеңдер бойынша тарылуы мұнда ескерілмейді. Жоба, парақ, кесте, баған тізбегі бойынша гранттар не беретіні көрсетілген, ұяшықты қазір өңдеуге болатыны емес.'),
    (N'effectiveAccess.inheritedFrom', N'ru', N'Унаследовано от {resource}'),
    (N'effectiveAccess.inheritedFrom', N'kz', N'{resource} ресурсынан мұраланған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:fv616 ── кінець секції ──

-- COLL:fv616 ── Розріз ефективного доступу на аркуші, таблиці й колонці (ФВ-6.16, `EffectiveAccessPanel`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.effectiveAccessProject', N'en', N'A sheet, table or column is looked at inside one project: pass projectId.', 1),
    (N'err.ECR-TMPL-0404.effectiveAccessResource', N'en', N'{resource} was not found.', 1),
    (N'err.ECR-TMPL-0404.effectiveAccessNotInProject', N'en', N'{resource} is not part of the template of project {projectId}.', 1),
    (N'effectiveAccess.hintAll', N'en', N'Pick a registry, a project, or a sheet, table or column inside a project, to see the resulting level and which grant of which role gives it. This only explains the decision; it changes nothing.', 1),
    (N'effectiveAccess.kindSheet', N'en', N'Sheet', 1),
    (N'effectiveAccess.kindTable', N'en', N'Table', 1),
    (N'effectiveAccess.kindColumn', N'en', N'Column', 1),
    (N'effectiveAccess.projectId', N'en', N'Project ID', 1),
    (N'effectiveAccess.caveat', N'en', N'Document state (submitted, approved, closed period) and assignment narrowing by periods are not taken into account here. This shows what the grants give along the chain project, sheet, table, column, not whether a cell can be edited right now.', 1),
    (N'effectiveAccess.inheritedFrom', N'en', N'Inherited from {resource}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:fv616 ── кінець секції ──
