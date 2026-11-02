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
-- CONDFMT:client ── ФВ-2.7: збереження правил умовного форматування (`ConditionalFormatPanel`, If-Match) і підсвітка в сітці ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'conditionalFormat.none', N'en', N'This table has no rules yet.', 1),
    (N'conditionalFormat.saved', N'en', N'Conditional formatting rules saved.', 1),
    (N'conditionalFormat.incomplete', N'en', N'Complete or remove the rules marked above before saving.', 1),
    (N'conditionalFormat.readOnly', N'en', N'Rules can be changed only in a draft version and only with the right to edit templates.', 1),
    (N'conditionalFormat.conflictTitle', N'en', N'Someone else changed the rules', 1),
    (N'conditionalFormat.conflict', N'en', N'The rules of this template version were saved by someone else after you opened them. Your changes are kept here: Save replaces their rules with yours, Discard shows theirs.', 1),
    (N'conditionalFormat.discard', N'en', N'Discard my changes', 1),
    (N'conditionalFormat.blocker.Color', N'en', N'A colour must be written as #rrggbb.', 1),
    (N'err.ECR-REQ-0422.condFormatIfMatch', N'en', N'This request needs an If-Match header carrying the ETag of the conditional formatting rules you read.', 1),
    (N'err.ECR-TMPL-0409.condFormatChanged', N'en', N'The conditional formatting rules of this template version were changed after you read them. Reload them and repeat your change.', 1)
    -- ru/kz — окремою порцією `CONDFMT:client` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CONDFMT:client ── кінець секції ──
