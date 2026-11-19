-- B80:source-impact ── ru/kz: B80 N5 (D-554), вплив затвердження/повернення документа-джерела на залежні документи: відмови сервера й діалог клієнта; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SRC-0409.impactConfirmationRequired', N'ru', N'Действие затронет утверждённые периоды зависимых документов ({approvedDependents} из {dependents}). Проверьте влияние и подтвердите действие.'),
    (N'err.ECR-SRC-0409.impactConfirmationRequired', N'kz', N'Әрекет тәуелді құжаттардың бекітілген кезеңдеріне әсер етеді ({dependents} ішінен {approvedDependents}). Әсерді қарап, әрекетті растаңыз.'),
    (N'err.ECR-SRC-0409.impactChanged', N'ru', N'Влияние изменилось с момента, когда вы его смотрели. Проверьте актуальное влияние и подтвердите заново.'),
    (N'err.ECR-SRC-0409.impactChanged', N'kz', N'Әсер сіз оны қараған сәттен бері өзгерді. Өзекті әсерді қарап, қайта растаңыз.'),
    (N'err.ECR-SRC-0422.sourceImpactBlocked', N'ru', N'Действие запрещено политикой связи: утверждены зависимые периоды ({approvedDependents}). Сначала верните эти периоды в работу.'),
    (N'err.ECR-SRC-0422.sourceImpactBlocked', N'kz', N'Әрекетке байланыс саясаты тыйым салады: тәуелді кезеңдер бекітілген ({approvedDependents}). Алдымен осы кезеңдерді жұмысқа қайтарыңыз.'),
    (N'err.ECR-SRC-0422.impactReasonRequired', N'ru', N'Укажите причину подтверждения: политика связи требует её для записи в журнал.'),
    (N'err.ECR-SRC-0422.impactReasonRequired', N'kz', N'Растау себебін көрсетіңіз: байланыс саясаты оны журналға жазу үшін талап етеді.'),
    (N'err.ECR-REQ-0422.sourceImpactActionInvalid', N'ru', N'Параметр action: approve или reopen.'),
    (N'err.ECR-REQ-0422.sourceImpactActionInvalid', N'kz', N'action параметрі: approve немесе reopen.'),
    (N'sourceImpact.title.approve', N'ru', N'Влияние утверждения на зависимые документы'),
    (N'sourceImpact.title.approve', N'kz', N'Бекітудің тәуелді құжаттарға әсері'),
    (N'sourceImpact.title.reopen', N'ru', N'Влияние возврата в работу на зависимые документы'),
    (N'sourceImpact.title.reopen', N'kz', N'Жұмысқа қайтарудың тәуелді құжаттарға әсері'),
    (N'sourceImpact.intro.approve', N'ru', N'Этот лист — источник данных для других документов. Утверждённых зависимых периодов: {approved} из {total}. Утверждённый период не пересчитывается и устареет.'),
    (N'sourceImpact.intro.approve', N'kz', N'Бұл парақ — басқа құжаттардың дерек көзі. Тәуелді бекітілген кезеңдер: {total} ішінен {approved}. Бекітілген кезең қайта есептелмейді және ескіреді.'),
    (N'sourceImpact.intro.reopen', N'ru', N'Этот лист — источник данных для других документов. Утверждённых зависимых периодов: {approved} из {total}. После возврата месяц источника перестанет быть утверждённым, а они устареют.'),
    (N'sourceImpact.intro.reopen', N'kz', N'Бұл парақ — басқа құжаттардың дерек көзі. Тәуелді бекітілген кезеңдер: {total} ішінен {approved}. Қайтарудан кейін дерек көзінің айы бекітілмеген болады, ал олар ескіреді.'),
    (N'sourceImpact.policy.warn', N'ru', N'Подтвердите, что понимаете последствия.'),
    (N'sourceImpact.policy.warn', N'kz', N'Салдарын түсінетініңізді растаңыз.'),
    (N'sourceImpact.policy.confirmWithReason', N'ru', N'Подтвердите и укажите причину — она попадёт в журнал безопасности.'),
    (N'sourceImpact.policy.confirmWithReason', N'kz', N'Растап, себебін көрсетіңіз — ол қауіпсіздік журналына түседі.'),
    (N'sourceImpact.policy.block', N'ru', N'Политика связи запрещает это действие, пока утверждены зависимые периоды. Сначала верните их в работу.'),
    (N'sourceImpact.policy.block', N'kz', N'Тәуелді кезеңдер бекітілген кезде байланыс саясаты бұл әрекетке тыйым салады. Алдымен оларды жұмысқа қайтарыңыз.'),
    (N'sourceImpact.list', N'ru', N'Зависимые документы и периоды'),
    (N'sourceImpact.list', N'kz', N'Тәуелді құжаттар мен кезеңдер'),
    (N'sourceImpact.status.approved', N'ru', N'утверждён'),
    (N'sourceImpact.status.approved', N'kz', N'бекітілген'),
    (N'sourceImpact.status.pending', N'ru', N'пересчитается'),
    (N'sourceImpact.status.pending', N'kz', N'қайта есептеледі'),
    (N'sourceImpact.hidden', N'ru', N'Ещё зависимых периодов, к которым у вас нет доступа: {count}'),
    (N'sourceImpact.hidden', N'kz', N'Қолжетімділігіңіз жоқ тағы тәуелді кезеңдер: {count}'),
    (N'sourceImpact.reason.label', N'ru', N'Причина подтверждения'),
    (N'sourceImpact.reason.label', N'kz', N'Растау себебі'),
    (N'sourceImpact.confirm.approve', N'ru', N'Утвердить всё равно'),
    (N'sourceImpact.confirm.approve', N'kz', N'Бәрібір бекіту'),
    (N'sourceImpact.confirm.reopen', N'ru', N'Вернуть всё равно'),
    (N'sourceImpact.confirm.reopen', N'kz', N'Бәрібір қайтару'),
    (N'sourceImpact.loadFailed', N'ru', N'Не удалось проверить влияние на зависимые документы; сервер проверит его при выполнении действия.'),
    (N'sourceImpact.loadFailed', N'kz', N'Тәуелді құжаттарға әсерді тексеру мүмкін болмады; әрекет орындалғанда сервер оны тексереді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:source-impact ── кінець секції ──

-- B80:source-impact ── en: відмови сервера й діалог клієнта (область 1); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SRC-0409.impactConfirmationRequired', N'en', N'This action affects approved periods of dependent documents ({approvedDependents} of {dependents}). Review the impact and confirm the action.', 1),
    (N'err.ECR-SRC-0409.impactChanged', N'en', N'The impact has changed since you reviewed it. Review the current impact and confirm again.', 1),
    (N'err.ECR-SRC-0422.sourceImpactBlocked', N'en', N'The binding policy forbids this action while dependent periods are approved ({approvedDependents}). Return those periods to work first.', 1),
    (N'err.ECR-SRC-0422.impactReasonRequired', N'en', N'Enter a reason for the confirmation: the binding policy requires it for the security log.', 1),
    (N'err.ECR-REQ-0422.sourceImpactActionInvalid', N'en', N'The action parameter must be approve or reopen.', 1),
    (N'sourceImpact.title.approve', N'en', N'Impact of approval on dependent documents', 1),
    (N'sourceImpact.title.reopen', N'en', N'Impact of returning to work on dependent documents', 1),
    (N'sourceImpact.intro.approve', N'en', N'This sheet is a data source for other documents. Approved dependent periods: {approved} of {total}. An approved period is not recalculated and will become stale.', 1),
    (N'sourceImpact.intro.reopen', N'en', N'This sheet is a data source for other documents. Approved dependent periods: {approved} of {total}. After the return, the source month stops being approved and they will become stale.', 1),
    (N'sourceImpact.policy.warn', N'en', N'Confirm that you understand the consequences.', 1),
    (N'sourceImpact.policy.confirmWithReason', N'en', N'Confirm and give a reason: it goes to the security log.', 1),
    (N'sourceImpact.policy.block', N'en', N'The binding policy forbids this action while dependent periods are approved. Return them to work first.', 1),
    (N'sourceImpact.list', N'en', N'Dependent documents and periods', 1),
    (N'sourceImpact.status.approved', N'en', N'approved', 1),
    (N'sourceImpact.status.pending', N'en', N'will be recalculated', 1),
    (N'sourceImpact.hidden', N'en', N'More dependent periods you have no access to: {count}', 1),
    (N'sourceImpact.reason.label', N'en', N'Reason for the confirmation', 1),
    (N'sourceImpact.confirm.approve', N'en', N'Approve anyway', 1),
    (N'sourceImpact.confirm.reopen', N'en', N'Return anyway', 1),
    (N'sourceImpact.loadFailed', N'en', N'Could not check the impact on dependent documents; the server will check it when the action runs.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:source-impact ── кінець секції ──
