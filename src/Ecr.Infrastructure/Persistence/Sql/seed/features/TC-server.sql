-- TC:server ── ru/kz: чат таблиці документа, права й відмови сервера; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Document.Discuss', N'ru', N'Писать в чат таблицы, которую пользователь видит'),
    (N'permission.Document.Discuss', N'kz', N'Пайдаланушы көретін кестенің чатына жазу'),
    (N'permission.Document.DiscussModerate', N'ru', N'Удалять чужие сообщения в чатах таблиц'),
    (N'permission.Document.DiscussModerate', N'kz', N'Кесте чаттарындағы басқалардың хабарламаларын жою'),
    (N'err.ECR-REQ-0422.discussionBodyInvalid', N'ru', N'Сообщение должно содержать текст не длиннее {max} символов и упоминать не более {maxMentions} человек. Проверьте также период.'),
    (N'err.ECR-REQ-0422.discussionBodyInvalid', N'kz', N'Хабарламада {max} таңбадан аспайтын мәтін болуы және {maxMentions} адамнан көп аталмауы керек. Кезеңді де тексеріңіз.'),
    (N'err.ECR-REQ-0422.discussionEditWindowClosed', N'ru', N'Сообщение можно править только в течение {minutes} минут после написания.'),
    (N'err.ECR-REQ-0422.discussionEditWindowClosed', N'kz', N'Хабарламаны жазғаннан кейін тек {minutes} минут ішінде түзетуге болады.'),
    (N'err.ECR-REQ-0422.discussionMessageDeleted', N'ru', N'Сообщение удалено, править его больше нельзя.'),
    (N'err.ECR-REQ-0422.discussionMessageDeleted', N'kz', N'Хабарлама жойылған, оны енді түзетуге болмайды.'),
    (N'err.ECR-ACCS-0403.discussionEditForbidden', N'ru', N'Править сообщение может только его автор.'),
    (N'err.ECR-ACCS-0403.discussionEditForbidden', N'kz', N'Хабарламаны тек оның авторы түзете алады.'),
    (N'err.ECR-ACCS-0403.discussionDeleteForbidden', N'ru', N'Удалить сообщение может только автор или модератор.'),
    (N'err.ECR-ACCS-0403.discussionDeleteForbidden', N'kz', N'Хабарламаны тек автор немесе модератор жоя алады.'),
    (N'err.ECR-ACCS-0403.discussionPeriodForbidden', N'ru', N'Ваш доступ не охватывает этот период, поэтому писать в чат за него нельзя.'),
    (N'err.ECR-ACCS-0403.discussionPeriodForbidden', N'kz', N'Сіздің қолжетімділігіңіз бұл кезеңді қамтымайды, сондықтан ол үшін чатқа жазуға болмайды.'),
    (N'err.ECR-PRD-0409.projectArchivedNoChat', N'ru', N'Проект документа {documentId} в архиве: чаты его таблиц доступны только для чтения.'),
    (N'err.ECR-PRD-0409.projectArchivedNoChat', N'kz', N'{documentId} құжатының жобасы мұрағатта: оның кесте чаттары тек оқуға арналған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TC:server ── кінець секції ──
