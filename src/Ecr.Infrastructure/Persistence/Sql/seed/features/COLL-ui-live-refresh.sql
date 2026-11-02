-- COLL:ui-live-refresh ── ru/kz: живе оновлення (чат таблиці, «Refresh», банер над аркушем); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'tableChat.newBelow', N'ru', N'Новые сообщения: {n}'),
    (N'tableChat.newBelow', N'kz', N'Жаңа хабарламалар: {n}'),
    (N'liveRefresh.refresh', N'ru', N'Обновить'),
    (N'liveRefresh.refresh', N'kz', N'Жаңарту'),
    (N'liveRefresh.refreshTitle', N'ru', N'Перечитать таблицы этого листа с сервера (Shift+F5). Ваши несохранённые правки сохраняются.'),
    (N'liveRefresh.refreshTitle', N'kz', N'Осы парақтың кестелерін серверден қайта оқу (Shift+F5). Сақталмаған түзетулеріңіз қалады.'),
    (N'liveRefresh.updatedBy', N'ru', N'Обновил(а) {user} в {time}'),
    (N'liveRefresh.updatedBy', N'kz', N'{user} {time} уақытта жаңартты'),
    (N'liveRefresh.updatedByOther', N'ru', N'Обновлено другим пользователем в {time}'),
    (N'liveRefresh.updatedByOther', N'kz', N'Басқа пайдаланушы {time} уақытта жаңартты'),
    (N'liveRefresh.updatedNoTime', N'ru', N'Этот лист обновил другой пользователь'),
    (N'liveRefresh.updatedNoTime', N'kz', N'Бұл парақты басқа пайдаланушы жаңартты'),
    (N'grid.keys.refresh', N'ru', N'Перечитать таблицы с сервера'),
    (N'grid.keys.refresh', N'kz', N'Кестелерді серверден қайта оқу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-live-refresh ── кінець секції ──
