-- DOCREOPEN:doc-period-reopen ── ru/kz: закритий місяць, відкритий для одного документа (приймальна 08.11 (i)) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.reopen.action', N'ru', N'Открыть {period} для этого документа…'),
    (N'document.reopen.noPermission', N'ru', N'Открыть закрытый месяц может только пользователь, которому в этом проекте разрешено открывать периоды. Обратитесь к ответственному за периоды.'),
    (N'document.reopen.dialogTitle', N'ru', N'Открыть {period} для этого документа'),
    (N'document.reopen.dialogScope', N'ru', N'Открывается только этот документ. Все остальные документы за {period} остаются закрытыми. Действие записывается в журнал аудита.'),
    (N'document.reopen.reason', N'ru', N'Причина'),
    (N'document.reopen.until', N'ru', N'Открыт до'),
    (N'document.reopen.untilHint', N'ru', N'Ваше местное время. В этот момент месяц закроется сам; по умолчанию — в конце суток площадки.'),
    (N'document.reopen.untilInvalid', N'ru', N'Выберите момент в будущем.'),
    (N'document.reopen.confirm', N'ru', N'Открыть для этого документа'),
    (N'document.reopen.activeTitle', N'ru', N'Открыт для этого документа до {until}'),
    (N'document.reopen.activeBody', N'ru', N'Для всех остальных документов {period} остаётся закрытым. Причина: «{reason}». Открыл: {by}.'),
    (N'document.reopen.done', N'ru', N'{period} открыт для этого документа до {until}.'),
    (N'err.ECR-PRD-0422.reopenReasonTooLong', N'ru', N'Причина слишком длинная: не больше {max} символов.'),
    (N'document.reopen.action', N'kz', N'{period} осы құжат үшін ашу…'),
    (N'document.reopen.noPermission', N'kz', N'Жабық айды осы жобада кезеңдерді ашуға рұқсаты бар пайдаланушы ғана аша алады. Кезеңдерге жауапты адамға жүгініңіз.'),
    (N'document.reopen.dialogTitle', N'kz', N'{period} осы құжат үшін ашу'),
    (N'document.reopen.dialogScope', N'kz', N'Тек осы құжат ашылады. {period} бойынша қалған құжаттардың бәрі жабық қалады. Әрекет аудит журналына жазылады.'),
    (N'document.reopen.reason', N'kz', N'Себеп'),
    (N'document.reopen.until', N'kz', N'Ашық мерзімі'),
    (N'document.reopen.untilHint', N'kz', N'Жергілікті уақытыңыз. Осы сәтте ай өзі жабылады; әдепкі бойынша — алаң тәулігінің соңында.'),
    (N'document.reopen.untilInvalid', N'kz', N'Болашақтағы сәтті таңдаңыз.'),
    (N'document.reopen.confirm', N'kz', N'Осы құжат үшін ашу'),
    (N'document.reopen.activeTitle', N'kz', N'Осы құжат үшін {until} дейін ашық'),
    (N'document.reopen.activeBody', N'kz', N'Қалған құжаттар үшін {period} жабық қалады. Себеп: «{reason}». Ашқан: {by}.'),
    (N'document.reopen.done', N'kz', N'{period} осы құжат үшін {until} дейін ашылды.'),
    (N'err.ECR-PRD-0422.reopenReasonTooLong', N'kz', N'Себеп тым ұзын: {max} таңбадан аспауы керек.'),
    (N'document.reopen.audit.reopened', N'ru', N'Открыт для этого документа до {until}. Причина: «{reason}»'),
    (N'document.reopen.audit.closedExpired', N'ru', N'Срок открытия истёк: месяц снова закрыт для этого документа'),
    (N'document.reopen.audit.closedReplaced', N'ru', N'Заменено новым открытием для этого документа'),
    (N'document.reopen.audit.reopened', N'kz', N'Осы құжат үшін {until} дейін ашылды. Себеп: «{reason}»'),
    (N'document.reopen.audit.closedExpired', N'kz', N'Ашық мерзімі өтті: ай осы құжат үшін қайта жабылды'),
    (N'document.reopen.audit.closedReplaced', N'kz', N'Осы құжат үшін жаңа ашумен ауыстырылды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCREOPEN:doc-period-reopen ── кінець секції ──
-- DOCREOPEN:doc-period-reopen ── закритий місяць, відкритий для одного документа (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.reopen.action', N'en', N'Reopen {period} for this document…', 1),
    (N'document.reopen.noPermission', N'en', N'Only a user allowed to reopen periods in this project can reopen a locked month. Ask your period manager.', 1),
    (N'document.reopen.dialogTitle', N'en', N'Reopen {period} for this document', 1),
    (N'document.reopen.dialogScope', N'en', N'Only this document is opened. Every other document stays locked for {period}. The action is recorded in the audit trail.', 1),
    (N'document.reopen.reason', N'en', N'Reason', 1),
    (N'document.reopen.until', N'en', N'Open until', 1),
    (N'document.reopen.untilHint', N'en', N'Your local time. The month locks again by itself at this moment; by default at the end of the site day.', 1),
    (N'document.reopen.untilInvalid', N'en', N'Choose a moment in the future.', 1),
    (N'document.reopen.confirm', N'en', N'Reopen for this document', 1),
    (N'document.reopen.activeTitle', N'en', N'Reopened for this document until {until}', 1),
    (N'document.reopen.activeBody', N'en', N'{period} stays locked for every other document. Reason: «{reason}». Opened by {by}.', 1),
    (N'document.reopen.done', N'en', N'{period} is reopened for this document until {until}.', 1),
    (N'err.ECR-PRD-0422.reopenReasonTooLong', N'en', N'The reason is too long: at most {max} characters.', 1),
    (N'document.reopen.audit.reopened', N'en', N'Reopened for this document until {until}. Reason: «{reason}»', 1),
    (N'document.reopen.audit.closedExpired', N'en', N'Open-until reached: the month is locked again for this document', 1),
    (N'document.reopen.audit.closedReplaced', N'en', N'Replaced by a new reopen for this document', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCREOPEN:doc-period-reopen ── кінець секції ──
