-- CONDFMT:client ── ru/kz збереження правил умовного форматування (ФВ-2.7); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'conditionalFormat.none', N'ru', N'У этой таблицы пока нет правил.'),
    (N'conditionalFormat.none', N'kz', N'Бұл кестеде әзірге ережелер жоқ.'),
    (N'conditionalFormat.saved', N'ru', N'Правила условного форматирования сохранены.'),
    (N'conditionalFormat.saved', N'kz', N'Шартты пішімдеу ережелері сақталды.'),
    (N'conditionalFormat.incomplete', N'ru', N'Перед сохранением дополните или удалите отмеченные выше правила.'),
    (N'conditionalFormat.incomplete', N'kz', N'Сақтамас бұрын жоғарыда белгіленген ережелерді толықтырыңыз немесе жойыңыз.'),
    (N'conditionalFormat.readOnly', N'ru', N'Правила можно менять только в черновой версии и только с правом редактирования шаблонов.'),
    (N'conditionalFormat.readOnly', N'kz', N'Ережелерді тек нобай нұсқасында және үлгілерді өңдеу құқығымен ғана өзгертуге болады.'),
    (N'conditionalFormat.conflictTitle', N'ru', N'Правила изменил кто-то другой'),
    (N'conditionalFormat.conflictTitle', N'kz', N'Ережелерді басқа біреу өзгертті'),
    (N'conditionalFormat.conflict', N'ru', N'Правила этой версии шаблона сохранил кто-то другой после того, как вы их открыли. Ваши изменения остались здесь: «Сохранить» заменит их правила вашими, «Отменить» покажет их правила.'),
    (N'conditionalFormat.conflict', N'kz', N'Бұл үлгі нұсқасының ережелерін Сіз ашқаннан кейін басқа біреу сақтады. Сіздің өзгерістеріңіз осында қалды: «Сақтау» олардың ережелерін Сіздікімен ауыстырады, «Бас тарту» олардың ережелерін көрсетеді.'),
    (N'conditionalFormat.discard', N'ru', N'Отменить мои изменения'),
    (N'conditionalFormat.discard', N'kz', N'Менің өзгерістерімнен бас тарту'),
    (N'conditionalFormat.blocker.Color', N'ru', N'Цвет нужно указать в виде #rrggbb.'),
    (N'conditionalFormat.blocker.Color', N'kz', N'Түсті #rrggbb түрінде көрсету керек.'),
    (N'err.ECR-REQ-0422.condFormatIfMatch', N'ru', N'Для этого запроса нужен заголовок If-Match с ETag прочитанных вами правил условного форматирования.'),
    (N'err.ECR-REQ-0422.condFormatIfMatch', N'kz', N'Бұл сұрауға Сіз оқыған шартты пішімдеу ережелерінің ETag мәні бар If-Match тақырыбы қажет.'),
    (N'err.ECR-TMPL-0409.condFormatChanged', N'ru', N'Правила условного форматирования этой версии шаблона изменились после того, как вы их прочитали. Загрузите их заново и повторите изменение.'),
    (N'err.ECR-TMPL-0409.condFormatChanged', N'kz', N'Бұл үлгі нұсқасының шартты пішімдеу ережелері Сіз оларды оқығаннан кейін өзгерді. Оларды қайта жүктеп, өзгерісті қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CONDFMT:client ── кінець секції ──
