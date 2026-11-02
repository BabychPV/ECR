-- COLL:reqclose ── ru/kz вкладки «Security events» екрана аудиту (ФВ-5.24); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.viewSecurity', N'ru', N'События безопасности'),
    (N'audit.viewSecurity', N'kz', N'Қауіпсіздік оқиғалары'),
    (N'audit.eventType', N'ru', N'Тип события'),
    (N'audit.eventType', N'kz', N'Оқиға түрі'),
    (N'audit.securityEmpty', N'ru', N'В этом окне нет событий безопасности.'),
    (N'audit.securityEmpty', N'kz', N'Бұл терезеде қауіпсіздік оқиғалары жоқ.'),
    (N'audit.details', N'ru', N'Детали'),
    (N'audit.details', N'kz', N'Мәліметтер'),
    (N'audit.correlation', N'ru', N'Идентификатор корреляции'),
    (N'audit.correlation', N'kz', N'Корреляция идентификаторы'),
    (N'audit.viewWorkflow', N'ru', N'События согласования'),
    (N'audit.viewWorkflow', N'kz', N'Келісу оқиғалары'),
    (N'audit.workflowEmpty', N'ru', N'В этом окне нет событий согласования.'),
    (N'audit.workflowEmpty', N'kz', N'Бұл терезеде келісу оқиғалары жоқ.'),
    (N'audit.action', N'ru', N'Действие'),
    (N'audit.action', N'kz', N'Әрекет'),
    (N'audit.comment', N'ru', N'Комментарий'),
    (N'audit.comment', N'kz', N'Пікір'),
    (N'audit.sheet', N'ru', N'Лист'),
    (N'audit.sheet', N'kz', N'Парақ'),
    (N'audit.statusChange', N'ru', N'Статус'),
    (N'audit.statusChange', N'kz', N'Күй'),
    (N'audit.systemActor', N'ru', N'Система'),
    (N'audit.systemActor', N'kz', N'Жүйе'),
    (N'audit.sortBy', N'ru', N'Сортировать: {column}'),
    (N'audit.sortBy', N'kz', N'Сұрыптау: {column}'),
    (N'audit.sortedAsc', N'ru', N'по возрастанию'),
    (N'audit.sortedAsc', N'kz', N'өсу бойынша'),
    (N'audit.sortedDesc', N'ru', N'по убыванию'),
    (N'audit.sortedDesc', N'kz', N'кему бойынша'),
    (N'audit.workflowAction.Submit', N'ru', N'Подано'),
    (N'audit.workflowAction.Submit', N'kz', N'Ұсынылды'),
    (N'audit.workflowAction.Approve', N'ru', N'Утверждено'),
    (N'audit.workflowAction.Approve', N'kz', N'Бекітілді'),
    (N'audit.workflowAction.ApproveStep', N'ru', N'Шаг согласован'),
    (N'audit.workflowAction.ApproveStep', N'kz', N'Қадам келісілді'),
    (N'audit.workflowAction.Reject', N'ru', N'Отклонено'),
    (N'audit.workflowAction.Reject', N'kz', N'Қабылданбады'),
    (N'audit.workflowAction.Reopen', N'ru', N'Возвращено в работу'),
    (N'audit.workflowAction.Reopen', N'kz', N'Жұмысқа қайтарылды'),
    (N'audit.workflowAction.Recall', N'ru', N'Отозвано'),
    (N'audit.workflowAction.Recall', N'kz', N'Кері шақырылды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:reqclose ── кінець секції ──

