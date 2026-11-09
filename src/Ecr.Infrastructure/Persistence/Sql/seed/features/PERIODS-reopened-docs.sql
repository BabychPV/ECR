-- PERIODS:reopened-docs ── ru/kz: вкладка Periods — закритий місяць, відкритий для окремих документів, і «Close now» (приймальна i4) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'periods.reopenedDocs.summary.one', N'ru', N'Закрыт · {count} документ открыт до {until}'),
    (N'periods.reopenedDocs.summary.few', N'ru', N'Закрыт · {count} документа открыты до {until}'),
    (N'periods.reopenedDocs.summary.many', N'ru', N'Закрыт · {count} документов открыты до {until}'),
    (N'periods.reopenedDocs.summary.other', N'ru', N'Закрыт · {count} документа открыты до {until}'),
    (N'periods.reopenedDocs.itemUntil', N'ru', N'до {until}'),
    (N'periods.reopenedDocs.listLabel', N'ru', N'Документы, открытые за {period}'),
    (N'periods.reopenedDocs.closeNow', N'ru', N'Закрыть сейчас'),
    (N'periods.reopenedDocs.noPermission', N'ru', N'Закрыть досрочно может только пользователь, которому в этом проекте разрешено открывать периоды.'),
    (N'periods.reopenedDocs.confirmTitle', N'ru', N'Закрыть {period} для документа {document} сейчас?'),
    (N'periods.reopenedDocs.confirmText', N'ru', N'Месяц снова закроется для этого документа сразу, а не в {until}. Действие записывается в журнал.'),
    (N'periods.reopenedDocs.closed', N'ru', N'{period} снова закрыт для документа {document}.'),
    (N'document.reopen.audit.closedManually', N'ru', N'Закрыто досрочно: месяц снова закрыт для этого документа'),
    (N'periods.reopenedDocs.summary.one', N'kz', N'Жабық · {count} құжат {until} дейін ашық'),
    (N'periods.reopenedDocs.summary.few', N'kz', N'Жабық · {count} құжат {until} дейін ашық'),
    (N'periods.reopenedDocs.summary.many', N'kz', N'Жабық · {count} құжат {until} дейін ашық'),
    (N'periods.reopenedDocs.summary.other', N'kz', N'Жабық · {count} құжат {until} дейін ашық'),
    (N'periods.reopenedDocs.itemUntil', N'kz', N'{until} дейін'),
    (N'periods.reopenedDocs.listLabel', N'kz', N'{period} үшін ашылған құжаттар'),
    (N'periods.reopenedDocs.closeNow', N'kz', N'Қазір жабу'),
    (N'periods.reopenedDocs.noPermission', N'kz', N'Мерзімінен бұрын жабуды тек осы жобада кезеңдерді ашуға рұқсаты бар пайдаланушы жасай алады.'),
    (N'periods.reopenedDocs.confirmTitle', N'kz', N'{document} құжаты үшін {period} қазір жабылсын ба?'),
    (N'periods.reopenedDocs.confirmText', N'kz', N'Ай осы құжат үшін {until} емес, бірден қайта жабылады. Әрекет журналға жазылады.'),
    (N'periods.reopenedDocs.closed', N'kz', N'{period} {document} құжаты үшін қайта жабылды.'),
    (N'document.reopen.audit.closedManually', N'kz', N'Мерзімінен бұрын жабылды: ай осы құжат үшін қайта жабылды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERIODS:reopened-docs ── кінець секції ──
-- PERIODS:reopened-docs ── вкладка Periods — закритий місяць, відкритий для окремих документів, і «Close now» (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'periods.reopenedDocs.summary.one', N'en', N'Locked · {count} document reopened until {until}', 1),
    (N'periods.reopenedDocs.summary.few', N'en', N'Locked · {count} documents reopened until {until}', 1),
    (N'periods.reopenedDocs.summary.many', N'en', N'Locked · {count} documents reopened until {until}', 1),
    (N'periods.reopenedDocs.summary.other', N'en', N'Locked · {count} documents reopened until {until}', 1),
    (N'periods.reopenedDocs.itemUntil', N'en', N'until {until}', 1),
    (N'periods.reopenedDocs.listLabel', N'en', N'Documents reopened for {period}', 1),
    (N'periods.reopenedDocs.closeNow', N'en', N'Close now', 1),
    (N'periods.reopenedDocs.noPermission', N'en', N'Only a user allowed to reopen periods in this project can close it early.', 1),
    (N'periods.reopenedDocs.confirmTitle', N'en', N'Close {period} for {document} now?', 1),
    (N'periods.reopenedDocs.confirmText', N'en', N'The month locks again for this document right away instead of at {until}. The action is recorded in the audit trail.', 1),
    (N'periods.reopenedDocs.closed', N'en', N'{period} is locked again for {document}.', 1),
    (N'document.reopen.audit.closedManually', N'en', N'Closed early: the month is locked again for this document', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERIODS:reopened-docs ── кінець секції ──
