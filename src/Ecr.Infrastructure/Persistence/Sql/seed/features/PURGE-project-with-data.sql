-- COLL:purge-project ── en: видалення проєкту з усіма даними (✎ 25.11, людина: видалити активні проєкти через інтерфейс) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Project.DeleteWithData',        N'en', N'Delete a project with all its data', 1),
    (N'err.ECR-PRD-0409.purgeArchived',           N'en', N'Project {code} cannot be deleted: its data is archived (periods: {periods}) and the archive is immutable.', 1),
    (N'err.ECR-PRD-0409.purgeHasDependants',      N'en', N'Project {code} cannot be deleted: {count} document(s) of other projects read its data ({dependants}). Remove the dependency first.', 1),
    (N'err.ECR-PRD-0422.purgeCodeMismatch',       N'en', N'The typed code does not match the project code {code}.', 1),
    (N'err.ECR-PRD-0422.purgeReasonRequired',     N'en', N'Enter a reason for deleting the project (up to {maxLength} characters): it goes to the security log.', 1),
    (N'projects.lifecycle.purge',                 N'en', N'Delete project with all data…', 1),
    (N'projects.lifecycle.purge.desc',            N'en', N'Administrators only. Permanently deletes the project, its documents, cell values, calculation results, issues and approval history. Archived projects and projects read by other documents cannot be deleted.', 1),
    (N'projects.purge.title',                     N'en', N'Delete project {code} with all data', 1),
    (N'projects.purge.loading',                   N'en', N'Counting what will be deleted...', 1),
    (N'projects.purge.irreversible',              N'en', N'This cannot be undone. Everything listed below is deleted permanently; only the security log keeps a summary.', 1),
    (N'projects.purge.documents',                 N'en', N'Documents ({count})', 1),
    (N'projects.purge.noDocuments',               N'en', N'The project has no documents.', 1),
    (N'projects.purge.documentCells',             N'en', N'{key} · {template} · {cells} cells', 1),
    (N'projects.purge.counts',                    N'en', N'Cells: {cells} · calculation results: {results} · calculation runs: {runs} · issues: {issues} · approval events: {events} · sheet states: {states} · submission snapshots: {snapshots} · periods: {periods}', 1),
    (N'projects.purge.months',                    N'en', N'Months with data: {months}', 1),
    (N'projects.purge.dependants',                N'en', N'Other projects read this one — deletion is blocked:', 1),
    (N'projects.purge.dependant',                 N'en', N'{project} / {key} ({reason})', 1),
    (N'projects.purge.archived',                  N'en', N'The project data is archived (periods: {periods}) — the archive is immutable, deletion is blocked.', 1),
    (N'projects.purge.reason',                    N'en', N'Reason (goes to the security log)', 1),
    (N'projects.purge.typeCode',                  N'en', N'Type the project code {code} to confirm', 1),
    (N'projects.purge.confirm',                   N'en', N'Delete permanently', 1),
    (N'projects.purged',                          N'en', N'Project {code} and all its data were deleted', 1),
    -- ✎ R3 25.11 (C2-b, C2-e, C2-f, C2-g)
    (N'err.ECR-PRD-0409.purgeHasEventMap',        N'en', N'Project {code} cannot be deleted: its documents ({documents}) have source event maps. Remove the maps in Sources first, then delete the project.', 1),
    (N'err.ECR-PRD-0409.purgeConcurrentChange',   N'en', N'The project changed while it was being deleted (for example, a new document was created). Nothing was deleted — open the dialog again and repeat.', 1),
    (N'projects.purge.eventMaps',                 N'en', N'Documents of this project have source event maps — deletion is blocked. Remove the maps in Sources first:', 1),
    (N'projects.purge.eventMapsLink',             N'en', N'Open Sources', 1),
    (N'projects.purge.approvedMonths',            N'en', N'Approved months that will be deleted without reopening: {months}', 1),
    (N'projects.purge.warning.hse400Tools',       N'en', N'This project is used by the HSE400 template tools (tools/hse400: golden tests, version migration). After deletion they must be pointed at another project or the project re-created.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:purge-project ── кінець секції ──

-- COLL:purge-project ── ru/kz: той самий діалог; en — порцією вище ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Project.DeleteWithData',    N'ru', N'Удалять проект со всеми данными'),
    (N'permission.Project.DeleteWithData',    N'kz', N'Жобаны барлық деректерімен жою'),
    (N'err.ECR-PRD-0409.purgeArchived',       N'ru', N'Проект {code} нельзя удалить: его данные в архиве (периоды: {periods}), а архив неизменен.'),
    (N'err.ECR-PRD-0409.purgeArchived',       N'kz', N'{code} жобасын жою мүмкін емес: оның деректері мұрағатта (кезеңдер: {periods}), ал мұрағат өзгермейді.'),
    (N'err.ECR-PRD-0409.purgeHasDependants',  N'ru', N'Проект {code} нельзя удалить: его данные читают документы других проектов ({count}: {dependants}). Сначала уберите зависимость.'),
    (N'err.ECR-PRD-0409.purgeHasDependants',  N'kz', N'{code} жобасын жою мүмкін емес: оның деректерін басқа жобалардың құжаттары оқиды ({count}: {dependants}). Алдымен тәуелділікті алып тастаңыз.'),
    (N'err.ECR-PRD-0422.purgeCodeMismatch',   N'ru', N'Введённый код не совпадает с кодом проекта {code}.'),
    (N'err.ECR-PRD-0422.purgeCodeMismatch',   N'kz', N'Енгізілген код {code} жоба кодымен сәйкес келмейді.'),
    (N'err.ECR-PRD-0422.purgeReasonRequired', N'ru', N'Укажите причину удаления проекта (до {maxLength} символов): она попадёт в журнал безопасности.'),
    (N'err.ECR-PRD-0422.purgeReasonRequired', N'kz', N'Жобаны жою себебін көрсетіңіз ({maxLength} таңбаға дейін): ол қауіпсіздік журналына түседі.'),
    (N'projects.lifecycle.purge',             N'ru', N'Удалить проект со всеми данными…'),
    (N'projects.lifecycle.purge',             N'kz', N'Жобаны барлық деректерімен жою…'),
    (N'projects.lifecycle.purge.desc',        N'ru', N'Только для администратора. Безвозвратно удаляет проект, его документы, значения ячеек, результаты расчётов, проблемы и историю согласования. Архивные проекты и проекты, которые читают другие документы, удалить нельзя.'),
    (N'projects.lifecycle.purge.desc',        N'kz', N'Тек әкімші үшін. Жобаны, оның құжаттарын, ұяшық мәндерін, есептеу нәтижелерін, мәселелерді және келісу тарихын қайтымсыз жояды. Мұрағаттағы жобаларды және басқа құжаттар оқитын жобаларды жоюға болмайды.'),
    (N'projects.purge.title',                 N'ru', N'Удалить проект {code} со всеми данными'),
    (N'projects.purge.title',                 N'kz', N'{code} жобасын барлық деректерімен жою'),
    (N'projects.purge.loading',               N'ru', N'Подсчитываем, что будет удалено...'),
    (N'projects.purge.loading',               N'kz', N'Не жойылатынын есептеп жатырмыз...'),
    (N'projects.purge.irreversible',          N'ru', N'Это действие необратимо. Всё перечисленное ниже удаляется навсегда; сводку сохраняет только журнал безопасности.'),
    (N'projects.purge.irreversible',          N'kz', N'Бұл әрекетті қайтару мүмкін емес. Төменде көрсетілгеннің бәрі біржола жойылады; қорытындыны тек қауіпсіздік журналы сақтайды.'),
    (N'projects.purge.documents',             N'ru', N'Документы ({count})'),
    (N'projects.purge.documents',             N'kz', N'Құжаттар ({count})'),
    (N'projects.purge.noDocuments',           N'ru', N'В проекте нет документов.'),
    (N'projects.purge.noDocuments',           N'kz', N'Жобада құжаттар жоқ.'),
    (N'projects.purge.documentCells',         N'ru', N'{key} · {template} · ячеек: {cells}'),
    (N'projects.purge.documentCells',         N'kz', N'{key} · {template} · ұяшықтар: {cells}'),
    (N'projects.purge.counts',                N'ru', N'Ячейки: {cells} · результаты расчётов: {results} · прогоны расчётов: {runs} · проблемы: {issues} · события согласования: {events} · состояния листов: {states} · срезы подачи: {snapshots} · периоды: {periods}'),
    (N'projects.purge.counts',                N'kz', N'Ұяшықтар: {cells} · есептеу нәтижелері: {results} · есептеу жүрістері: {runs} · мәселелер: {issues} · келісу оқиғалары: {events} · парақ күйлері: {states} · ұсыну кесінділері: {snapshots} · кезеңдер: {periods}'),
    (N'projects.purge.months',                N'ru', N'Месяцы с данными: {months}'),
    (N'projects.purge.months',                N'kz', N'Деректері бар айлар: {months}'),
    (N'projects.purge.dependants',            N'ru', N'Этот проект читают другие проекты — удаление заблокировано:'),
    (N'projects.purge.dependants',            N'kz', N'Бұл жобаны басқа жобалар оқиды — жою бұғатталған:'),
    (N'projects.purge.dependant',             N'ru', N'{project} / {key} ({reason})'),
    (N'projects.purge.dependant',             N'kz', N'{project} / {key} ({reason})'),
    (N'projects.purge.archived',              N'ru', N'Данные проекта в архиве (периоды: {periods}) — архив неизменен, удаление заблокировано.'),
    (N'projects.purge.archived',              N'kz', N'Жоба деректері мұрағатта (кезеңдер: {periods}) — мұрағат өзгермейді, жою бұғатталған.'),
    (N'projects.purge.reason',                N'ru', N'Причина (попадёт в журнал безопасности)'),
    (N'projects.purge.reason',                N'kz', N'Себебі (қауіпсіздік журналына түседі)'),
    (N'projects.purge.typeCode',              N'ru', N'Для подтверждения введите код проекта {code}'),
    (N'projects.purge.typeCode',              N'kz', N'Растау үшін {code} жоба кодын енгізіңіз'),
    (N'projects.purge.confirm',               N'ru', N'Удалить навсегда'),
    (N'projects.purge.confirm',               N'kz', N'Біржола жою'),
    (N'projects.purged',                      N'ru', N'Проект {code} и все его данные удалены'),
    (N'projects.purged',                      N'kz', N'{code} жобасы және оның барлық деректері жойылды'),
    (N'err.ECR-PRD-0409.purgeHasEventMap',    N'ru', N'Проект {code} нельзя удалить: у его документов ({documents}) есть карты событий источника. Сначала уберите карты в Sources, затем удалите проект.'),
    (N'err.ECR-PRD-0409.purgeHasEventMap',    N'kz', N'{code} жобасын жою мүмкін емес: оның құжаттарында ({documents}) дереккөз оқиғаларының карталары бар. Алдымен карталарды Sources бөлімінде алып тастаңыз, содан кейін жобаны жойыңыз.'),
    (N'err.ECR-PRD-0409.purgeConcurrentChange', N'ru', N'Проект изменился во время удаления (например, создан новый документ). Ничего не удалено — откройте диалог заново и повторите.'),
    (N'err.ECR-PRD-0409.purgeConcurrentChange', N'kz', N'Жоба жою кезінде өзгерді (мысалы, жаңа құжат жасалды). Ештеңе жойылған жоқ — диалогты қайта ашып, қайталаңыз.'),
    (N'projects.purge.eventMaps',             N'ru', N'У документов этого проекта есть карты событий источника — удаление заблокировано. Сначала уберите карты в Sources:'),
    (N'projects.purge.eventMaps',             N'kz', N'Бұл жоба құжаттарында дереккөз оқиғаларының карталары бар — жою бұғатталған. Алдымен карталарды Sources бөлімінде алып тастаңыз:'),
    (N'projects.purge.eventMapsLink',         N'ru', N'Открыть Sources'),
    (N'projects.purge.eventMapsLink',         N'kz', N'Sources ашу'),
    (N'projects.purge.approvedMonths',        N'ru', N'Утверждённые месяцы, которые будут удалены без возврата: {months}'),
    (N'projects.purge.approvedMonths',        N'kz', N'Қайтарусыз жойылатын бекітілген айлар: {months}'),
    (N'projects.purge.warning.hse400Tools',   N'ru', N'Этот проект используют инструменты шаблона HSE400 (tools/hse400: золотые тесты, перенос версии). После удаления их нужно перенастроить на другой проект или создать проект заново.'),
    (N'projects.purge.warning.hse400Tools',   N'kz', N'Бұл жобаны HSE400 үлгісінің құралдары пайдаланады (tools/hse400: алтын тесттер, нұсқаны көшіру). Жойғаннан кейін оларды басқа жобаға бағыттау немесе жобаны қайта жасау керек.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:purge-project ── кінець секції ──
