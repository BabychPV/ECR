-- WFREASONS:sheet-history ── ru/kz: журнал погодження аркуша в Inspector → History; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'inspector.sheetHistoryTitle', N'ru', N'Журнал листа'),
    (N'inspector.sheetHistoryTitle', N'kz', N'Парақ журналы'),
    (N'inspector.sheetHistoryHint', N'ru', N'Выберите ячейку, чтобы включить «Эта ячейка».'),
    (N'inspector.sheetHistoryHint', N'kz', N'«Осы ұяшық» режимін қосу үшін ұяшықты таңдаңыз.'),
    (N'inspector.sheetHistoryEmpty', N'ru', N'На этом листе ещё нет правок и действий согласования.'),
    (N'inspector.sheetHistoryEmpty', N'kz', N'Бұл параққа әлі түзетулер мен келісу әрекеттері жасалмаған.'),
    (N'inspector.history.modeLabel', N'ru', N'Что показывает история'),
    (N'inspector.history.modeLabel', N'kz', N'Тарих нені көрсетеді'),
    (N'inspector.history.modeSheet', N'ru', N'Журнал листа'),
    (N'inspector.history.modeSheet', N'kz', N'Парақ журналы'),
    (N'inspector.history.modeCell', N'ru', N'Эта ячейка'),
    (N'inspector.history.modeCell', N'kz', N'Осы ұяшық'),
    (N'inspector.history.scopeSheet', N'ru', N'Лист: {sheet} · {period}'),
    (N'inspector.history.scopeSheet', N'kz', N'Парақ: {sheet} · {period}'),
    (N'inspector.history.scopeCell', N'ru', N'Ячейка: {cell} · {column}'),
    (N'inspector.history.scopeCell', N'kz', N'Ұяшық: {cell} · {column}'),
    (N'inspector.history.includeSystem', N'ru', N'Показывать системные изменения'),
    (N'inspector.history.includeSystem', N'kz', N'Жүйелік өзгерістерді көрсету'),
    (N'inspector.history.loadMore', N'ru', N'Показать ещё'),
    (N'inspector.history.loadMore', N'kz', N'Тағы көрсету'),
    (N'workflow.eventAction.Submit', N'ru', N'Подан'),
    (N'workflow.eventAction.Submit', N'kz', N'Тапсырылды'),
    (N'workflow.eventAction.Approve', N'ru', N'Утверждён'),
    (N'workflow.eventAction.Approve', N'kz', N'Бекітілді'),
    (N'workflow.eventAction.ApproveStep', N'ru', N'Шаг согласован'),
    (N'workflow.eventAction.ApproveStep', N'kz', N'Қадам келісілді'),
    (N'workflow.eventAction.Reject', N'ru', N'Отклонён'),
    (N'workflow.eventAction.Reject', N'kz', N'Қабылданбады'),
    (N'workflow.eventAction.Reopen', N'ru', N'Возвращён в работу'),
    (N'workflow.eventAction.Reopen', N'kz', N'Жұмысқа қайтарылды'),
    (N'workflow.eventAction.Recall', N'ru', N'Отозван'),
    (N'workflow.eventAction.Recall', N'kz', N'Кері шақырылды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- WFREASONS:sheet-history ── кінець секції ──
