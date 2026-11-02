-- COLL:ui41keys ── ru/kz UI-41: підказка клавіш під таблицею і довідка Keyboard shortcuts; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.keys.hint', N'ru', N'Стрелки — переход · Enter или F2 — правка · Ctrl+V — вставка из Excel · F9 — пересчёт'),
    (N'grid.keys.hint', N'kz', N'Көрсеткілер — жылжу · Enter не F2 — өңдеу · Ctrl+V — Excel-ден қою · F9 — қайта есептеу'),
    (N'grid.keys.hintReadOnly', N'ru', N'Стрелки — переход · Ctrl+C — копирование'),
    (N'grid.keys.hintReadOnly', N'kz', N'Көрсеткілер — жылжу · Ctrl+C — көшіру'),
    (N'grid.keys.help', N'ru', N'Сочетания клавиш'),
    (N'grid.keys.help', N'kz', N'Пернетақта тіркесімдері'),
    (N'grid.keys.move', N'ru', N'Переход между ячейками'),
    (N'grid.keys.move', N'kz', N'Ұяшықтар арасында жылжу'),
    (N'grid.keys.edit', N'ru', N'Править ячейку; повторный Enter сохраняет'),
    (N'grid.keys.edit', N'kz', N'Ұяшықты өңдеу; қайта Enter сақтайды'),
    (N'grid.keys.tab', N'ru', N'Сохранить и перейти к следующей ячейке'),
    (N'grid.keys.tab', N'kz', N'Сақтап, келесі ұяшыққа өту'),
    (N'grid.keys.cancel', N'ru', N'Отменить правку'),
    (N'grid.keys.cancel', N'kz', N'Өңдеуден бас тарту'),
    (N'grid.keys.copy', N'ru', N'Копировать выделенные ячейки'),
    (N'grid.keys.copy', N'kz', N'Белгіленген ұяшықтарды көшіру'),
    (N'grid.keys.paste', N'ru', N'Вставить из Excel'),
    (N'grid.keys.paste', N'kz', N'Excel-ден қою'),
    (N'grid.keys.undo', N'ru', N'Отменить или вернуть последнюю правку'),
    (N'grid.keys.undo', N'kz', N'Соңғы өзгерісті болдырмау немесе қайтару'),
    (N'grid.keys.save', N'ru', N'Сохранить сейчас'),
    (N'grid.keys.save', N'kz', N'Қазір сақтау'),
    (N'grid.keys.recalc', N'ru', N'Пересчитать этот лист'),
    (N'grid.keys.recalc', N'kz', N'Осы парақты қайта есептеу'),
    (N'grid.keys.palette', N'ru', N'Поиск или команда'),
    (N'grid.keys.palette', N'kz', N'Іздеу немесе пәрмен')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui41keys ── кінець секції ──
