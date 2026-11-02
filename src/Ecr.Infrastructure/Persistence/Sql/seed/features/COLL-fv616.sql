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

