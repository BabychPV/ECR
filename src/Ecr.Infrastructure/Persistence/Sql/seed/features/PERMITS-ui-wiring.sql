-- PERMITS: ui-wiring ── ru/kz: зведення PM-08b + PM-08c; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.editor.deleteTable.title', N'ru', N'Удалить таблицу «{name}»?'),
    (N'permits.editor.deleteTable.title', N'kz', N'«{name}» кестесін жою керек пе?'),
    (N'permits.editor.deleteTable.verb', N'ru', N'Удалить таблицу'),
    (N'permits.editor.deleteTable.verb', N'kz', N'Кестені жою'),
    (N'permits.editor.deleteTable.confirm', N'ru', N'Таблица «{name}», её столбцы и проверки уйдут из черновика. Для разрешений ничего не изменится, пока вы не опубликуете.'),
    (N'permits.editor.deleteTable.confirm', N'kz', N'«{name}» кестесі, оның бағандары мен тексерулері жобадан шығады. Жарияламайынша рұқсаттар үшін ештеңе өзгермейді.'),
    (N'permits.editor.deleteTable.published', N'ru', N'В этой версии можно удалить только таблицы, добавленные в текущем черновике и ещё не сохранённые.'),
    (N'permits.editor.deleteTable.published', N'kz', N'Осы нұсқада тек ағымдағы жобада қосылған және әлі сақталмаған кестелерді жоюға болады.'),
    (N'permits.editor.deleteTable.referenced', N'ru', N'Таблица «{name}» служит источником списка в {columns}. Сначала измените эти столбцы.'),
    (N'permits.editor.deleteTable.referenced', N'kz', N'«{name}» кестесі {columns} ішіндегі тізімнің көзі. Алдымен осы бағандарды өзгертіңіз.'),
    (N'permits.editor.dialogs.unsaved', N'ru', N'Последние изменения не удалось сохранить, поэтому ничего не отправлено. Исправьте проблемы на вкладке Structure или перечитайте тип и повторите.'),
    (N'permits.editor.dialogs.unsaved', N'kz', N'Соңғы өзгерістер сақталмады, сондықтан ештеңе жіберілген жоқ. Structure қойындысындағы мәселелерді түзетіңіз немесе түрді қайта оқып, қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: ui-wiring ── кінець секції ──
-- PERMITS: ui-wiring ── зведення PM-08b + PM-08c: видалення таблиці, відмова збереження перед відправкою; ru/kz — порцією PERMITS: ui-wiring нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.editor.deleteTable.title', N'en', N'Remove table "{name}"?', 1),
    (N'permits.editor.deleteTable.verb', N'en', N'Remove table', 1),
    (N'permits.editor.deleteTable.confirm', N'en', N'The table "{name}", its columns and its checks leave the draft. Nothing changes for permits until you publish.', 1),
    (N'permits.editor.deleteTable.published', N'en', N'Only tables added in the current draft and not saved yet can be removed in this version.', 1),
    (N'permits.editor.deleteTable.referenced', N'en', N'The table "{name}" is the source of a list in {columns}. Change those columns first.', 1),
    (N'permits.editor.dialogs.unsaved', N'en', N'The latest changes could not be saved, so nothing was sent. Fix the problems on the Structure tab or reload the type, then try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: ui-wiring ── кінець секції ──
