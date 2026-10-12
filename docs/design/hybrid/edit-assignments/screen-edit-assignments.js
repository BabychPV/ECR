/* =====================================================================
   screen-edit-assignments.js — EA-0 · «Доступ до редагування»: передача місяця виконавцям, заповнення, валідація.
   Не окремий маршрут: це частина екрана документа (/documents/:id). screen-document.js викликає E.ea.* у п'яти місцях:
   пункт More → «Edit access», смуга над таблицею (#doc-ea-bar), замок комірки (lockText/cellCls), реєстрація в screen render,
   renderDoc. Дані — лише з ECR.data (люди, періоди, документи); стан передач живе в модулі на час сеансу.
   Проєкт: DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md §3, §7, §8. Нового кольору й нового виду бейджа немає:
   статус виконавця/зрізу = StatusBadge('sheet', …, {label}); стан комірки = наявна штрихована сіра is-locked з підказкою.
   ?panel=  : edit-assignments (+ view=months|people)
   ?dialog= : assign-wizard (+ step=who|review, set=<id>, nv=1, ea422=1) · assign-repeat · assign-validate · assign-return · assign-remove ·
              assign-fill-confirm · editor-set · result-assigned · result-validated · result-returned
   Додатково: ea=mixed|assigned|filled|awaiting|returned|validated|other|none|empty (демо-стан вересня DOC-000001) · lang=en|ru|kz · as=Approver|DataEntry
   ===================================================================== */
(function (E) {
  'use strict';
  var h = E.h, icon = E.icon, F = E.fmt, D = E.data;

  /* ================= i18n: ключі en/ru/kz (у продукті — секція «-- EDA:» у 09-seed.sql) ================= */
  var MN = {
    en: ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'],
    ru: ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'],
    kz: ['Қаңтар', 'Ақпан', 'Наурыз', 'Сәуір', 'Мамыр', 'Маусым', 'Шілде', 'Тамыз', 'Қыркүйек', 'Қазан', 'Қараша', 'Желтоқсан']
  };
  var KEYS = [
    ['deny.LockedByOwner', 'Locked: this month is handed to other users', 'Заблокировано: месяц передан другим пользователям', 'Бұғатталған: ай басқа пайдаланушыларға берілген'],
    ['deny.AssignmentFilled', 'Locked: you marked this month as filled. The owner can return it for edits.', 'Заблокировано: вы отметили месяц как заполненный. Владелец может вернуть его на доработку.', 'Бұғатталған: сіз айды толтырылған деп белгіледіңіз. Иесі оны қайта өңдеуге қайтара алады.'],
    ['deny.AssignmentValidated', 'Locked: the owner validated this month', 'Заблокировано: владелец валидировал этот месяц', 'Бұғатталған: иесі бұл айды валидациялады'],
    ['editAssign.title', 'Edit access', 'Доступ к редактированию', 'Өңдеу қолжетімділігі'],
    ['editAssign.hint', 'Hand a month to someone to fill in, then validate it.', 'Передайте месяц исполнителю на заполнение, затем валидируйте его.', 'Айды толтыру үшін орындаушыға беріңіз, содан кейін валидациялаңыз.'],
    ['editAssign.owner', 'Owner: {name}', 'Владелец: {name}', 'Иесі: {name}'],
    ['editAssign.noRights', 'Only the document owner or a project manager can manage Edit access.', 'Доступом к редактированию управляют только владелец документа или менеджер проекта.', 'Өңдеу қолжетімділігін тек құжат иесі немесе жоба менеджері басқара алады.'],
    ['editAssign.view', 'View', 'Вид', 'Көрініс'],
    ['editAssign.months', 'Months', 'Месяцы', 'Айлар'],
    ['editAssign.people', 'People', 'Люди', 'Адамдар'],
    ['editAssign.col.month', 'Month', 'Месяц', 'Ай'],
    ['editAssign.col.executors', 'Executors', 'Исполнители', 'Орындаушылар'],
    ['editAssign.col.status', 'Status', 'Статус', 'Күйі'],
    ['editAssign.col.changed', 'Changed', 'Изменено', 'Өзгертілді'],
    ['editAssign.col.actions', 'Actions', 'Действия', 'Әрекеттер'],
    ['editAssign.col.person', 'Person', 'Человек', 'Адам'],
    ['editAssign.status.open', 'Owner only', 'Только владелец', 'Тек иесі'],
    ['editAssign.status.assigned', 'Assigned', 'Передано', 'Берілді'],
    ['editAssign.status.inProgress', 'In progress', 'В работе', 'Орындалуда'],
    ['editAssign.status.filled', 'Awaiting validation', 'Ждёт валидации', 'Валидацияны күтуде'],
    ['editAssign.status.returned', 'Returned', 'Возвращено', 'Қайтарылды'],
    ['editAssign.status.validated', 'Validated', 'Валидировано', 'Валидацияланды'],
    ['editAssign.recipient.assigned', 'Assigned', 'Передано', 'Берілді'],
    ['editAssign.recipient.filled', 'Filled', 'Заполнено', 'Толтырылды'],
    ['editAssign.recipient.returned', 'Returned', 'Возвращено', 'Қайтарылды'],
    ['editAssign.legend', 'Legend', 'Обозначения', 'Белгілер'],
    ['editAssign.assign', 'Assign month…', 'Передать месяц…', 'Айды беру…'],
    ['editAssign.repeatNext', 'Repeat for next month', 'Повторить на следующий месяц', 'Келесі айға қайталау'],
    ['editAssign.manageSets', 'Manage sets', 'Наборы', 'Жинақтарды басқару'],
    ['editAssign.edit', 'Change executors…', 'Изменить исполнителей…', 'Орындаушыларды өзгерту…'],
    ['editAssign.remove', 'Remove handover…', 'Снять передачу…', 'Беруді алып тастау…'],
    ['editAssign.validate', 'Validate…', 'Валидировать…', 'Валидациялау…'],
    ['editAssign.return', 'Return with comment…', 'Вернуть с комментарием…', 'Пікірмен қайтару…'],
    ['editAssign.awaiting', 'Awaiting validation: {n}', 'Ждут валидации: {n}', 'Валидацияны күтуде: {n}'],
    ['editAssign.open', 'Open Edit access', 'Открыть доступ к редактированию', 'Өңдеу қолжетімділігін ашу'],
    ['editAssign.visibility.visible', 'Can open the document', 'Видит документ', 'Құжатты көреді'],
    ['editAssign.visibility.notVisible', 'No access to the document', 'Нет доступа к документу', 'Құжатқа қолжетімділік жоқ'],
    ['editAssign.visibility.unknown', 'Access unknown (AD group)', 'Доступ неизвестен (группа AD)', 'Қолжетімділік белгісіз (AD тобы)'],
    ['editAssign.visibility.hint', 'Ask an administrator to give them the role “Executor”.', 'Попросите администратора выдать роль «Исполнитель».', 'Әкімшіден «Орындаушы» рөлін беруін сұраңыз.'],
    ['editAssign.exclusive', 'Also allow others with role rights', 'Разрешить также остальным по ролям', 'Рөлдері бойынша басқаларға да рұқсат ету'],
    ['editAssign.exclusive.hint', 'Off: until the month is validated or removed, only you and the executors can edit it.', 'Выключено: пока месяц не валидирован или не снят, править его могут только вы и исполнители.', 'Өшірулі: ай валидацияланғанша немесе алынғанша оны тек сіз бен орындаушылар өңдей алады.'],
    ['editAssign.note', 'Note for executors', 'Примечание для исполнителей', 'Орындаушыларға ескертпе'],
    ['editAssign.none.title', 'No months handed over', 'Месяцы не передавались', 'Берілген айлар жоқ'],
    ['editAssign.none.text', 'Hand a month to colleagues to fill in; you validate it afterwards.', 'Передайте месяц коллегам на заполнение; затем вы его валидируете.', 'Айды әріптестерге толтыруға беріңіз; кейін өзіңіз валидациялайсыз.'],
    ['editAssign.wizard.title', 'Assign a month', 'Передача месяца', 'Айды беру'],
    ['editAssign.wizard.what', 'What', 'Что', 'Не'],
    ['editAssign.wizard.who', 'To whom', 'Кому', 'Кімге'],
    ['editAssign.wizard.summary', 'Summary', 'Итог', 'Қорытынды'],
    ['editAssign.wizard.reviewText', 'Nothing is applied until you confirm. Check the summary below.', 'Ничего не применится, пока вы не подтвердите. Проверьте итог ниже.', 'Растағанша ештеңе қолданылмайды. Төмендегі қорытындыны тексеріңіз.'],
    ['editAssign.wizard.months', 'Months to hand over', 'Передаваемые месяцы', 'Берілетін айлар'],
    ['editAssign.wizard.closedMonth', 'This period is closed', 'Период закрыт', 'Кезең жабық'],
    ['editAssign.wizard.onlySheet', 'Only sheet', 'Только лист', 'Тек парақ'],
    ['editAssign.wizard.allSheets', 'All sheets', 'Все листы', 'Барлық парақтар'],
    ['editAssign.wizard.set', 'Saved set', 'Сохранённый набор', 'Сақталған жинақ'],
    ['editAssign.wizard.noSet', '— none, pick people below —', '— нет, выберите людей ниже —', '— жоқ, төменнен адамдарды таңдаңыз —'],
    ['editAssign.wizard.search', 'Find people', 'Найти людей', 'Адамдарды іздеу'],
    ['editAssign.wizard.searchHint', 'Type at least 2 characters of the name.', 'Введите не менее 2 символов имени.', 'Аттың кемінде 2 таңбасын енгізіңіз.'],
    ['editAssign.wizard.chosen', 'Executors', 'Исполнители', 'Орындаушылар'],
    ['editAssign.wizard.noMatch', 'Nobody matches “{q}”.', 'Никто не найден по «{q}».', '«{q}» бойынша ешкім табылмады.'],
    ['editAssign.wizard.remove', 'Remove {name}', 'Убрать {name}', '{name} алып тастау'],
    ['editAssign.wizard.saveSet', 'Save as set', 'Сохранить как набор', 'Жинақ ретінде сақтау'],
    ['editAssign.wizard.setName', 'Set name', 'Название набора', 'Жинақ атауы'],
    ['editAssign.wizard.setHint', 'Next month you pick this set instead of people.', 'В следующем месяце вы выберете набор вместо людей.', 'Келесі айда адамдардың орнына осы жинақты таңдайсыз.'],
    ['editAssign.wizard.sumMonths', 'Months', 'Месяцы', 'Айлар'],
    ['editAssign.wizard.sumExecutors', 'Executors', 'Исполнители', 'Орындаушылар'],
    ['editAssign.wizard.sumEffect', 'They will be able to edit these cells even without write rights in their role (within your own rights).', 'Они смогут править эти ячейки, даже если по роли у них нет права записи (в пределах ваших прав).', 'Олар рөлі бойынша жазу құқығы болмаса да осы ұяшықтарды өңдей алады (сіздің құқықтарыңыз шегінде).'],
    ['editAssign.wizard.sumExclusive', 'Others can’t edit {months} until it is validated or removed.', 'Остальные не смогут править {months}, пока он не валидирован или не снят.', 'Басқалар {months} валидацияланғанша немесе алынғанша өңдей алмайды.'],
    ['editAssign.wizard.sumOpen', 'Others with write rights in their role can still edit {months}.', 'Остальные с правом записи по роли по-прежнему смогут править {months}.', 'Рөлі бойынша жазу құқығы бар басқалар {months} өңдей алады.'],
    ['editAssign.wizard.noVisible', 'Executors who cannot open this document yet: {n}. The handover is saved anyway; ask an administrator to give them the role “Executor”.', 'Исполнителей, которые пока не видят документ: {n}. Передача всё равно сохранится; попросите администратора выдать им роль «Исполнитель».', 'Құжатты әлі көре алмайтын орындаушылар: {n}. Беру бәрібір сақталады; әкімшіден оларға «Орындаушы» рөлін беруін сұраңыз.'],
    ['editAssign.bar.assigned', '{month} was handed to you', 'Месяц «{month}» передан вам', '«{month}» айы сізге берілді'],
    ['editAssign.bar.assignedText', 'Fill in the cells, then mark the month as filled.', 'Заполните ячейки и отметьте месяц как заполненный.', 'Ұяшықтарды толтырып, айды толтырылған деп белгілеңіз.'],
    ['editAssign.bar.returned', '{month} was returned by the owner', 'Месяц «{month}» возвращён владельцем', '«{month}» айын иесі қайтарды'],
    ['editAssign.bar.filled', '{month}: filled — awaiting validation', '«{month}»: заполнено — ждёт валидации', '«{month}»: толтырылды — валидацияны күтуде'],
    ['editAssign.bar.filledText', 'You can’t edit this month until the owner returns it.', 'Вы не можете править этот месяц, пока владелец его не вернёт.', 'Иесі қайтармайынша бұл айды өңдей алмайсыз.'],
    ['editAssign.bar.validated', '{month}: validated by the owner', '«{month}»: валидировано владельцем', '«{month}»: иесі валидациялады'],
    ['editAssign.bar.validatedText', 'The month is closed for editing.', 'Месяц закрыт для правки.', 'Ай өңдеуге жабық.'],
    ['editAssign.bar.markFilled', 'Mark as filled', 'Отметить как заполненное', 'Толтырылды деп белгілеу'],
    ['editAssign.bar.confirmFill', 'After this you can’t edit {month} until the owner returns it.', 'После этого вы не сможете править «{month}», пока владелец его не вернёт.', 'Осыдан кейін иесі қайтармайынша «{month}» айын өңдей алмайсыз.'],
    ['editAssign.bar.ownerFilled', '{month} is filled — awaiting your validation', 'Месяц «{month}» заполнен — ждёт вашей валидации', '«{month}» айы толтырылды — сіздің валидацияңызды күтуде'],
    ['editAssign.bar.ownerFilledText', 'All executors marked it as filled. Check the figures, then validate it or return it with a comment.', 'Все исполнители отметили его как заполненный. Проверьте цифры, затем валидируйте или верните с комментарием.', 'Барлық орындаушылар оны толтырылды деп белгіледі. Сандарды тексеріп, валидациялаңыз немесе пікірмен қайтарыңыз.'],
    ['editAssign.toast.filled', '{month} marked as filled — the owner is notified', 'Месяц «{month}» отмечен как заполненный — владелец уведомлён', '«{month}» айы толтырылды деп белгіленді — иесіне хабарланды'],
    ['editAssign.validate.title', 'Validate {month}?', 'Валидировать «{month}»?', '«{month}» айын валидациялау керек пе?'],
    ['editAssign.validate.c1', 'All executors marked {month} as filled.', 'Все исполнители отметили «{month}» как заполненный.', 'Барлық орындаушылар «{month}» айын толтырылды деп белгіледі.'],
    ['editAssign.validate.c2', 'Executors can’t edit {month} after validation.', 'После валидации исполнители не смогут править «{month}».', 'Валидациядан кейін орындаушылар «{month}» айын өңдей алмайды.'],
    ['editAssign.validate.c3', 'You can return it with a comment later.', 'Позже вы сможете вернуть его с комментарием.', 'Кейінірек оны пікірмен қайтара аласыз.'],
    ['editAssign.validate.verb', 'Validate', 'Валидировать', 'Валидациялау'],
    ['editAssign.validate.blocked', 'Not everyone has finished', 'Заполнили не все', 'Бәрі бітірген жоқ'],
    ['editAssign.validate.blockedText', '{names} did not mark {month} as filled yet.', '{names} ещё не отметили «{month}» как заполненный.', '{names} «{month}» айын әлі толтырылды деп белгілеген жоқ.'],
    ['editAssign.return.title', 'Return {month} with a comment', 'Вернуть «{month}» с комментарием', '«{month}» айын пікірмен қайтару'],
    ['editAssign.return.label', 'Comment for the executors', 'Комментарий для исполнителей', 'Орындаушыларға пікір'],
    ['editAssign.return.hint', '1–500 characters. Only the returned executors and managers see it.', 'От 1 до 500 символов. Видят только возвращаемые исполнители и менеджеры.', '1–500 таңба. Тек қайтарылатын орындаушылар мен менеджерлер көреді.'],
    ['editAssign.return.to', 'Return to', 'Вернуть', 'Кімге қайтару'],
    ['editAssign.return.verb', 'Return', 'Вернуть', 'Қайтару'],
    ['editAssign.remove.title', 'Remove the handover of {month}?', 'Снять передачу месяца «{month}»?', '«{month}» айын беруді алып тастау керек пе?'],
    ['editAssign.remove.c1', 'Executors lose the right to edit {month}.', 'Исполнители теряют право править «{month}».', 'Орындаушылар «{month}» айын өңдеу құқығынан айырылады.'],
    ['editAssign.remove.c2', 'Values already entered stay.', 'Уже введённые значения сохраняются.', 'Енгізілген мәндер сақталады.'],
    ['editAssign.remove.c3', 'The audit trail keeps the record of who had access.', 'Журнал аудита сохранит запись о том, кто имел доступ.', 'Аудит журналы кімде қолжетімділік болғанын сақтайды.'],
    ['editAssign.remove.verb', 'Remove handover', 'Снять передачу', 'Беруді алып тастау'],
    ['editAssign.result.assigned', 'Handed over: {months}', 'Передано: {months}', 'Берілді: {months}'],
    ['editAssign.result.assignedText', 'Executors see it in My tasks. You get a note when all of them mark it as filled.', 'Исполнители увидят это в «Моих задачах». Когда все отметят месяц как заполненный, вы получите уведомление.', 'Орындаушылар мұны «Менің тапсырмаларымда» көреді. Бәрі толтырылды деп белгілегенде сізге хабарланады.'],
    ['editAssign.result.validated', 'Validated: {month}', 'Валидировано: {month}', 'Валидацияланды: {month}'],
    ['editAssign.result.validatedText', 'Executors are notified. {month} stays locked for them.', 'Исполнители уведомлены. Для них «{month}» остаётся заблокированным.', 'Орындаушыларға хабарланды. Олар үшін «{month}» бұғатталған күйінде қалады.'],
    ['editAssign.result.returned', 'Returned: {month}', 'Возвращено: {month}', 'Қайтарылды: {month}'],
    ['editAssign.result.returnedText', 'The returned executors see your comment above the table and in My tasks.', 'Возвращённые исполнители увидят ваш комментарий над таблицей и в «Моих задачах».', 'Қайтарылған орындаушылар пікіріңізді кестенің үстінде және «Менің тапсырмаларымда» көреді.'],
    ['editAssign.toast.removed', 'Handover of {month} removed', 'Передача месяца «{month}» снята', '«{month}» айын беру алынды'],
    ['editAssign.err.noMonths', 'Choose at least one month.', 'Выберите хотя бы один месяц.', 'Кемінде бір айды таңдаңыз.'],
    ['editAssign.err.noExecutors', 'Choose at least one executor.', 'Выберите хотя бы одного исполнителя.', 'Кемінде бір орындаушыны таңдаңыз.'],
    ['editAssign.err.setName', 'Give the set a name or untick “Save as set”.', 'Назовите набор или снимите «Сохранить как набор».', 'Жинаққа ат беріңіз немесе «Жинақ ретінде сақтау» белгісін алыңыз.'],
    ['editAssign.err.exceeds', 'You can’t hand over cells you can’t edit yourself: sheets {sheets}. Narrow the handover to one sheet you can edit.', 'Нельзя передавать ячейки, которые вы сами не можете править: листы {sheets}. Сузьте передачу до листа, который вам доступен.', 'Өзіңіз өңдей алмайтын ұяшықтарды беруге болмайды: {sheets} парақтары. Берілімді өзіңізге қолжетімді бір парақпен шектеңіз.'],
    ['editAssign.err.nextClosed', 'The next period is closed — it can’t be handed over.', 'Следующий период закрыт — передать его нельзя.', 'Келесі кезең жабық — оны беруге болмайды.'],
    ['editAssign.err.noNext', 'There is no next month in this project year.', 'В этом проектном году нет следующего месяца.', 'Бұл жоба жылында келесі ай жоқ.'],
    ['myTasks.assignToFill', 'Assigned to me', 'Назначено мне', 'Маған тағайындалған'],
    ['myTasks.awaitingValidation', 'Awaiting my validation', 'Ждёт моей валидации', 'Менің валидациямды күтуде'],
    ['myTasks.open', 'Open', 'Открыть', 'Ашу'],
    ['myTasks.returnComment', 'Owner’s comment', 'Комментарий владельца', 'Иесінің пікірі'],
    ['editorSet.title', 'Executor sets', 'Наборы исполнителей', 'Орындаушылар жинақтары'],
    ['editorSet.hint', 'A set is a saved list of people. Applying it copies the names, so later edits of a set do not change handovers already made.', 'Набор — сохранённый список людей. При применении имена копируются, поэтому правка набора не меняет уже сделанные передачи.', 'Жинақ — адамдардың сақталған тізімі. Қолданғанда аттар көшіріледі, сондықтан жинақты өзгерту бұрын жасалған берулерді өзгертпейді.'],
    ['editorSet.members', 'Members', 'Участники', 'Мүшелер'],
    ['editorSet.new', 'New set', 'Новый набор', 'Жаңа жинақ'],
    ['editorSet.save', 'Save set', 'Сохранить набор', 'Жинақты сақтау'],
    ['editorSet.delete', 'Delete', 'Удалить', 'Жою'],
    ['editorSet.deleteAsk', 'Delete set “{name}”?', 'Удалить набор «{name}»?', '«{name}» жинағын жою керек пе?'],
    ['editorSet.empty', 'No sets yet. Tick “Save as set” in the wizard or create one here.', 'Наборов пока нет. Отметьте «Сохранить как набор» в мастере или создайте здесь.', 'Жинақтар әлі жоқ. Шебердегі «Жинақ ретінде сақтау» белгісін қойыңыз немесе осында жасаңыз.'],
    ['editorSet.err.members', 'Pick at least one member.', 'Выберите хотя бы одного участника.', 'Кемінде бір мүшені таңдаңыз.']
  ];
  var I18N = { en: {}, ru: {}, kz: {} };
  KEYS.forEach(function (k) { I18N.en[k[0]] = k[1]; I18N.ru[k[0]] = k[2]; I18N.kz[k[0]] = k[3]; });
  var LANG = 'en';
  function t(key, v) { var s = (I18N[LANG] || I18N.en)[key]; if (s == null) { s = I18N.en[key]; } if (s == null) { console.warn('[EA i18n] немає ключа ' + key); return key; }
    return v ? s.replace(/\{(\w+)\}/g, function (m, k) { return v[k] != null ? v[k] : m; }) : s; }
  function monthName(p) { return (MN[LANG] || MN.en)[+String(p).split('-')[1] - 1]; }
  function monthsText(ps) { return ps.slice().sort().map(monthName).join(', '); }

  E.css('doc-ea', [
    '.doc-ea-live{min-width:0;max-width:100%}',
    '.doc-ea-wrap{min-width:0;max-width:100%;contain:inline-size;overflow:auto;border:1px solid var(--border);border-radius:var(--r2);background:var(--surface)}',
    '.doc-ea-wrap table.data thead th{background:var(--sunken);font-weight:500;color:var(--muted);position:sticky;top:0}',
    '.doc-ea-wrap table.data tbody th[scope="row"]{min-width:112px;max-width:none;font-weight:500;text-align:left}',
    '.doc-ea-wrap td.doc-ea-ex-cell{white-space:normal;max-width:none;padding-top:var(--s1);padding-bottom:var(--s1)}',
    '.doc-ea-ex{display:flex;align-items:center;gap:var(--s2);min-height:var(--row);flex-wrap:wrap}',
    '.doc-ea-ex .doc-ea-nm{min-width:0;white-space:nowrap}',
    '.doc-ea-note{color:var(--muted);font-size:var(--fs-xs);white-space:normal}',
    '.doc-ea-people{min-width:560px}',
    '.doc-ea-wrap table.doc-ea-people th,.doc-ea-wrap table.doc-ea-people td{padding-left:var(--s1);padding-right:var(--s1)}',
    '.doc-ea-wrap table.doc-ea-people th.doc-ea-rowh{height:auto;padding:var(--s1) var(--s2);min-width:150px;text-align:left}',
    '.doc-ea-people th.doc-ea-rowh{position:sticky;left:0;background:var(--surface);z-index:1;font-weight:500}',
    '.doc-ea-people td{text-align:center}',
    '.doc-ea-people th[scope="col"]{text-align:center}',
    '.doc-ea-mk{display:inline-flex;align-items:center;justify-content:center;min-width:var(--ctl);min-height:var(--ctl)}',
    '.doc-ea-legend{display:flex;flex-wrap:wrap;gap:var(--s3);color:var(--muted);font-size:var(--fs-xs)}',
    '.doc-ea-legend span{display:inline-flex;align-items:center;gap:var(--s1)}',
    '.doc-ea-chips{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:var(--s2)}',
    '.doc-ea-chips .btn[aria-pressed="true"]{background:var(--accent-soft);color:var(--accent-text);border-color:var(--accent)}',
    '.doc-ea-chosen{display:flex;flex-wrap:wrap;gap:var(--s2);min-height:var(--ctl)}',
    '.doc-ea-pill{display:inline-flex;align-items:center;gap:var(--s1);padding:0 var(--s1) 0 var(--s2);min-height:var(--ctl);border:1px solid var(--border);border-radius:var(--r1);background:var(--sunken)}',
    '.doc-ea-results{display:grid;gap:var(--s1)}',
    '.doc-ea-pick{display:flex;align-items:center;justify-content:space-between;gap:var(--s2);width:100%;min-height:var(--ctl);padding:0 var(--s2);border:1px solid var(--border);border-radius:var(--r1);background:var(--surface);text-align:left}',
    '.doc-ea-pick:hover{background:var(--hover)}',
    '.doc-ea-pick:disabled{color:var(--faint);background:var(--sunken)}',
    '.doc-ea-vis{color:var(--muted);font-size:var(--fs-xs)}',
    '.doc-ea-sech{display:flex;align-items:center;justify-content:space-between;gap:var(--s2);padding:var(--s3) var(--s4) var(--s1)}',
    '.doc-ea-task .doc-ea-quote{margin:0;padding:var(--s1) var(--s2);border-left:2px solid var(--warning);color:var(--text);white-space:normal}',
    '.doc-ea-task .doc-ea-acts{display:flex;gap:var(--s2);flex-wrap:wrap}',
    '.doc-ea-sets td.doc-ea-ex-cell{white-space:normal}',
    '.doc-ea-pickrow{display:flex;flex-wrap:wrap;gap:var(--s3)}',
    '@media (max-width:640px){.doc-ea-chips{grid-template-columns:repeat(3,minmax(0,1fr))}.doc-ea-wrap table.data th,.doc-ea-wrap table.data td{padding-left:var(--s2);padding-right:var(--s2)}.doc-ea-wrap table.data tbody th[scope="row"]{min-width:88px}}'
  ].join('\n'));

  /* ================= дані (детерміновані, лише похідні від ECR.data) ================= */
  var NOW = D.today || '2026-09-18T14:03:00';
  var PEOPLE = [ // кандидати в виконавці; видимість — за прямими призначеннями (D-214), групи AD чужої людини невідомі
    { n: 'D. Akhmetova', vis: 'Visible' }, { n: 'M. Petrenko', vis: 'Visible' }, { n: 'S. Nurlanov', vis: 'Visible' },
    { n: 'G. Tulegenova', vis: 'NotVisible' }, { n: 'O. Kovalenko', vis: 'Unknown' }, { n: 'B. Sadykov', vis: 'NotVisible' }];
  var SETS = [{ id: 'set-1', name: 'Dept 5 — air team', users: ['D. Akhmetova', 'M. Petrenko'] }, { id: 'set-2', name: 'Water & waste', users: ['S. Nurlanov', 'M. Petrenko'] }];
  var SCENARIOS = ['mixed', 'assigned', 'filled', 'awaiting', 'returned', 'validated', 'other', 'none', 'empty'];
  var COMMENT = 'Boiler B-2: the fuel volume looks ten times higher than in August. Please check the source meter and mark the month as filled again.';
  var seq = 10, store = {}, sim422 = false, host = null, ctx = null, drawer = null;
  function visOf(n) { var p = PEOPLE.filter(function (x) { return x.n === n; })[0]; return p ? p.vis : 'Unknown'; }
  function rec(u, st, o) { return Object.assign({ u: u, st: st, at: st === 'Filled' ? '2026-09-17T16:40' : null, reason: null }, o || {}); }
  function item(period, recs, o) { return Object.assign({ id: 'EA-' + (++seq), period: period, exclusive: true, note: '', sheet: '', recipients: recs, validated: false, changed: '2026-09-10T09:10', setName: '' }, o || {}); }
  function seedSep(name) {
    var n = { assigned: [rec('D. Akhmetova', 'Assigned'), rec('M. Petrenko', 'Assigned')],
      filled: [rec('D. Akhmetova', 'Filled'), rec('M. Petrenko', 'Assigned')],
      awaiting: [rec('D. Akhmetova', 'Filled'), rec('M. Petrenko', 'Filled')],
      returned: [rec('D. Akhmetova', 'Returned', { reason: COMMENT }), rec('M. Petrenko', 'Filled')],
      validated: [rec('D. Akhmetova', 'Filled'), rec('M. Petrenko', 'Filled')],
      other: [rec('S. Nurlanov', 'Assigned'), rec('M. Petrenko', 'Assigned')],
      mixed: [rec('D. Akhmetova', 'Assigned'), rec('M. Petrenko', 'Filled')] }[name];
    if (!n) return null;
    return item('2026-09', n, { note: 'Fuel gas and operating hours by the 25th.', changed: '2026-09-17T10:20', validated: name === 'validated', setName: name === 'other' ? '' : 'Dept 5 — air team' });
  }
  function seedDoc(id) {
    var list = [];
    if (id === 'DOC-000001') {
      list.push(item('2026-08', [rec('D. Akhmetova', 'Filled'), rec('M. Petrenko', 'Filled')], { validated: true, changed: '2026-09-02T11:30', setName: 'Dept 5 — air team' }));
      list.push(seedSep('mixed'));
      list.push(item('2026-10', [rec('S. Nurlanov', 'Assigned')], { changed: '2026-09-18T09:05', note: 'Planned outage: fill after the turnaround.' }));
    } else if (id === 'DOC-000002') list.push(item('2026-09', [rec('D. Akhmetova', 'Assigned')], { note: 'Please add the stack test results.', changed: '2026-09-16T12:00' }));
    else if (id === 'DOC-000004') list.push(item('2026-09', [rec('M. Petrenko', 'Filled'), rec('S. Nurlanov', 'Filled')], { changed: '2026-09-17T15:12' }));
    return list;
  }
  function itemsOf(docId) { if (!store[docId]) store[docId] = seedDoc(docId); return store[docId]; }
  function find(docId, period) { return itemsOf(docId).filter(function (x) { return x.period === period; })[0]; }
  function byId(docId, id) { return itemsOf(docId).filter(function (x) { return x.id === id; })[0]; }
  function applyScenario(docId, name) {
    if (SCENARIOS.indexOf(name) < 0) return;
    if (name === 'empty') { store[docId] = []; return; } // жодної передачі в документі
    var list = itemsOf(docId), old = find(docId, '2026-09'), neu = seedSep(name);
    if (old) list.splice(list.indexOf(old), 1);
    if (neu) list.push(neu);
  }
  function status(it) {
    if (it.validated) return 'Validated'; var r = it.recipients; if (!r.length) return 'Open';
    if (r.some(function (x) { return x.st === 'Returned'; })) return 'Returned';
    var f = r.filter(function (x) { return x.st === 'Filled'; }).length;
    return f === r.length ? 'Filled' : f ? 'InProgress' : 'Assigned';
  }
  // Нових бейджів немає: беремо наявні «sheet»-стани з власним підписом (нейтральний / info — чекає дії / warn / нейтральний ✓)
  var SLICE = { Open: ['Draft', 'open'], Assigned: ['Draft', 'assigned'], InProgress: ['Draft', 'inProgress'], Filled: ['Submitted', 'filled'], Returned: ['Returned', 'returned'], Validated: ['Approved', 'validated'] };
  var RECIP = { Assigned: ['Draft', 'assigned', 'draft'], Filled: ['Submitted', 'filled', 'submitted'], Returned: ['Returned', 'returned', 'returned'], Validated: ['Approved', 'filled', 'filled'] };
  function sliceBadge(st) { var b = SLICE[st]; return E.StatusBadge('sheet', b[0], { label: t('editAssign.status.' + b[1]), title: null }); }
  function recipBadge(r, validated) { var b = RECIP[validated ? 'Validated' : r.st]; return E.StatusBadge('sheet', b[0], { label: t('editAssign.recipient.' + (validated ? 'filled' : b[1])), quiet: true, title: null }); }
  function visBadge(u) { var v = visOf(u); if (v === 'NotVisible') return E.StatusBadge('health', 'Degraded', { label: t('editAssign.visibility.notVisible'), quiet: true, title: t('editAssign.visibility.hint') }); if (v === 'Unknown') return h('span', { class: 'doc-ea-vis' }, t('editAssign.visibility.unknown')); return null; }
  function visText(u) { return t('editAssign.visibility.' + { Visible: 'visible', NotVisible: 'notVisible', Unknown: 'unknown' }[visOf(u)]); }
  function viewer() { return E.me().name; }
  function isManager() { return E.role() !== 'DataEntry'; }
  function mine(it) { return it.recipients.filter(function (r) { return r.u === viewer(); })[0]; }
  function nowStamp() { return NOW; }
  function periodOf(id) { return D.period(id); }
  function nextPeriod(p) { var a = p.split('-'), m = +a[1]; return m >= 12 ? null : a[0] + '-' + ('0' + (m + 1)).slice(-2); }
  function openPeriod(p) { var x = periodOf(p); return !!x && x.state !== 'Archived' && x.state !== 'Closed'; }
  function awaitingList(docId) { return itemsOf(docId).filter(function (x) { return status(x) === 'Filled'; }); }

  /* ================= мутації (на сервері — окремі ендпоінти й аудит) ================= */
  function markFilled(it) { var r = mine(it); if (!r || r.st === 'Filled') return; r.st = 'Filled'; r.at = nowStamp(); r.reason = null; it.changed = nowStamp(); }
  function doValidate(it) { it.validated = true; it.changed = nowStamp(); }
  function doReturn(it, users, reason) { it.recipients.forEach(function (r) { if (users.indexOf(r.u) >= 0) { r.st = 'Returned'; r.reason = reason; r.at = null; } }); it.validated = false; it.changed = nowStamp(); }
  function applyWizard(docId, data) {
    var list = itemsOf(docId), touched = [];
    data.periods.slice().sort().forEach(function (p) {
      var it = find(docId, p), recs = data.users.map(function (u) { var old = it && it.recipients.filter(function (r) { return r.u === u; })[0]; return old || rec(u, 'Assigned'); });
      if (!it) { it = item(p, []); list.push(it); }
      if (it.validated && recs.some(function (r) { return r.st === 'Assigned'; })) it.validated = false; // додали виконавця → зріз знову не валідований
      it.recipients = recs; it.exclusive = !data.allowOthers; it.sheet = data.sheet || ''; it.note = data.note || it.note; it.changed = nowStamp(); it.setName = data.setName || '';
      touched.push(it);
    });
    if (data.saveSet) SETS.push({ id: 'set-' + (SETS.length + 1), name: data.newSetName.trim(), users: data.users.slice() });
    return touched;
  }

  /* ================= замок комірки: те, що бачить виконавець (R3–R5) ================= */
  function lockText(docId, period) {
    if (isManager()) return null; // R3: власник пише в межах своїх ролей
    var it = find(docId, period); if (!it) return null;
    var me = mine(it);
    if (me) return it.validated ? t('deny.AssignmentValidated') : me.st === 'Filled' ? t('deny.AssignmentFilled') : null;
    return it.exclusive ? t('deny.LockedByOwner') : null; // R5: підказка без імен (R9)
  }

  /* ================= смуга над таблицею ================= */
  function paintBar(slot) {
    if (!slot || !host) return; slot.textContent = ''; var docId = host.docId(), p = host.period(), it = find(docId, p), mon = monthName(p), node = null, c = ctx;
    if (it && mine(it)) {
      var me = mine(it);
      if (it.validated) node = E.Banner({ flush: true, icon: 'lock', id: 'doc-ea-banner', title: t('editAssign.bar.validated', { month: mon }), text: t('editAssign.bar.validatedText') });
      else if (me.st === 'Filled') node = E.Banner({ flush: true, icon: 'clock', id: 'doc-ea-banner', title: t('editAssign.bar.filled', { month: mon }), text: t('editAssign.bar.filledText') });
      else node = E.Banner({ flush: true, tone: me.st === 'Returned' ? 'warning' : 'info', icon: me.st === 'Returned' ? 'return' : 'tasks', id: 'doc-ea-banner',
        title: t(me.st === 'Returned' ? 'editAssign.bar.returned' : 'editAssign.bar.assigned', { month: mon }),
        text: me.st === 'Returned' ? '“' + me.reason + '”' : it.note || t('editAssign.bar.assignedText'),
        actions: [{ label: t('editAssign.bar.markFilled'), icon: 'check', id: 'doc-ea-mark-filled', onClick: function (e, btn) { askFill(btn); } }] });
    } else if (it && isManager() && status(it) === 'Filled') {
      node = E.Banner({ flush: true, tone: 'info', icon: 'inbox', id: 'doc-ea-banner', title: t('editAssign.bar.ownerFilled', { month: mon }), text: t('editAssign.bar.ownerFilledText'),
        actions: [{ label: t('editAssign.return'), icon: 'return', id: 'doc-ea-bar-return', onClick: function () { c.openDialog('assign-return'); } }, { label: t('editAssign.validate'), icon: 'checkCircle', id: 'doc-ea-bar-validate', onClick: function () { c.openDialog('assign-validate'); } }] });
    } else if (isManager() && awaitingList(docId).length) {
      node = E.Banner({ flush: true, tone: 'info', icon: 'inbox', id: 'doc-ea-banner', title: t('editAssign.awaiting', { n: awaitingList(docId).length }), text: awaitingList(docId).map(function (x) { return monthName(x.period); }).join(', '),
        actions: [{ label: t('editAssign.open'), id: 'doc-ea-bar-open', onClick: function () { c.openPanel('edit-assignments'); } }] });
    }
    if (node) slot.appendChild(node);
  }
  function askFill(btn) {
    var docId = host.docId(), p = host.period(), it = find(docId, p); if (!it || !mine(it)) return;
    E.InlineConfirm(btn, { text: t('editAssign.bar.confirmFill', { month: monthName(p) }), verb: t('editAssign.bar.markFilled'), onConfirm: function () {
      markFilled(it); host.rerender(); paintBadge(); E.Toast(t('editAssign.toast.filled', { month: monthName(p) })); } });
  }
  function moreItems() {
    if (!isManager() || !ctx) return []; var n = awaitingList(host.docId()).length;
    return [{ label: t('editAssign.title') + '…', icon: 'users', id: 'doc-more-edit-access', hint: n ? t('editAssign.awaiting', { n: n }) : null, onClick: function () { ctx.openPanel('edit-assignments'); } }];
  }

  /* ================= шторка «Edit access» ================= */
  function sortedItems() { return itemsOf(host.docId()).slice().sort(function (a, b) { return a.period < b.period ? -1 : 1; }); }
  function legend() {
    return h('div', { class: 'doc-ea-legend', role: 'group', 'aria-label': t('editAssign.legend') }, ['Assigned', 'Filled', 'Returned', 'Validated'].map(function (k) {
      return h('span', {}, h('i', { class: 'dot ' + RECIP[k][2] }), t('editAssign.recipient.' + (k === 'Validated' ? 'filled' : RECIP[k][1])) + (k === 'Validated' ? ' + ' + t('editAssign.status.validated').toLowerCase() : '')); }));
  }
  function rowMenu(it) {
    var st = status(it), docId = host.docId(), nf = it.recipients.some(function (r) { return r.st === 'Filled'; });
    return [{ label: t('editAssign.validate'), icon: 'checkCircle', disabled: st !== 'Filled', onClick: function () { ctx.openDialog('assign-validate', it.id); } },
      { label: t('editAssign.return'), icon: 'return', disabled: !nf, onClick: function () { ctx.openDialog('assign-return', it.id); } }, { sep: true },
      { label: t('editAssign.edit'), icon: 'pencil', onClick: function () { openWizard({ periods: [it.period], users: it.recipients.map(function (r) { return r.u; }), allowOthers: !it.exclusive, note: it.note, sheet: it.sheet, setName: it.setName }); } },
      { label: t('editAssign.repeatNext'), icon: 'copy', onClick: function () { repeatNext(it); } }, { sep: true },
      { label: t('editAssign.remove'), icon: 'trash', danger: true, onClick: function () { ctx.openDialog('assign-remove', it.id); } }];
  }
  function monthsTable() {
    var rows = sortedItems(), wrap = h('div', { class: 'doc-ea-wrap', tabindex: '-1' }), tb = h('table', { class: 'data', id: 'doc-ea-months' },
      h('caption', { class: 'sr' }, t('editAssign.months')),
      h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, t('editAssign.col.month')), h('th', { scope: 'col' }, t('editAssign.col.executors')), h('th', { scope: 'col' }, t('editAssign.col.status')), h('th', { scope: 'col', class: 'hide-sm' }, t('editAssign.col.changed')), h('th', { scope: 'col' }, h('span', { class: 'sr' }, t('editAssign.col.actions'))))),
      h('tbody', {}, rows.map(function (it) {
        var st = status(it), mon = monthName(it.period);
        return h('tr', { id: 'doc-ea-row-' + it.period }, h('th', { scope: 'row' }, E.twoLine(mon, F.period(it.period).split(' ')[1] + (it.exclusive ? '' : ' · ' + t('editAssign.exclusive').toLowerCase()))),
          h('td', { class: 'doc-ea-ex-cell' }, it.recipients.length ? it.recipients.map(function (r) { return h('div', { class: 'doc-ea-ex' }, h('span', { class: 'doc-ea-nm' }, r.u), recipBadge(r, it.validated), visBadge(r.u)); }) : h('span', { class: 'muted' }, t('editAssign.status.open')),
            it.note ? h('div', { class: 'doc-ea-note' }, it.note) : null),
          h('td', {}, sliceBadge(st)), h('td', { class: 'hide-sm mono muted' }, F.dateTime(it.changed)),
          h('td', {}, E.Button({ iconOnly: true, icon: 'more', label: t('editAssign.col.actions') + ': ' + mon, id: 'doc-ea-act-' + it.period, onClick: function (e, btn) { E.openMenu(btn, rowMenu(it)); } })));
      })));
    wrap.appendChild(tb); return wrap;
  }
  function peopleTable() {
    var rows = sortedItems(), users = []; rows.forEach(function (it) { it.recipients.forEach(function (r) { if (users.indexOf(r.u) < 0) users.push(r.u); }); }); users.sort();
    var head = h('tr', {}, h('th', { scope: 'col', class: 'doc-ea-rowh' }, t('editAssign.col.person')), D.periods.map(function (p) { return h('th', { scope: 'col', title: monthName(p.id) }, monthName(p.id).slice(0, 3)); }));
    var body = users.map(function (u) {
      return h('tr', {}, h('th', { scope: 'row', class: 'doc-ea-rowh' }, E.twoLine(u, visText(u))), D.periods.map(function (p) {
        var it = find(host.docId(), p.id), r = it && it.recipients.filter(function (x) { return x.u === u; })[0];
        if (!r) return h('td', {}, h('span', { class: 'faint', 'aria-hidden': 'true' }, '·'), h('span', { class: 'sr' }, '—'));
        var k = it.validated ? 'Validated' : r.st, lbl = t('editAssign.recipient.' + (k === 'Validated' ? 'filled' : RECIP[k][1])) + (k === 'Validated' ? ', ' + t('editAssign.status.validated').toLowerCase() : '');
        return h('td', {}, h('span', { class: 'doc-ea-mk', title: lbl }, h('i', { class: 'dot ' + RECIP[k][2] }), h('span', { class: 'sr' }, lbl))); }));
    });
    return h('div', {}, h('div', { class: 'doc-ea-wrap' }, h('table', { class: 'data doc-ea-people', id: 'doc-ea-people' }, h('caption', { class: 'sr' }, t('editAssign.people')), h('thead', {}, head), h('tbody', {}, body))), h('div', { class: 'p-2' }, legend()));
  }
  function paintDrawer() {
    if (!drawer || !drawer.ctl) return; var b = drawer.ctl.body, d = drawer; b.textContent = '';
    b.appendChild(h('div', { class: 'stack gap-3' }, h('p', { class: 'muted' }, t('editAssign.hint'))));
    var st = b.firstChild;
    if (!isManager()) { st.appendChild(E.EmptyState({ icon: 'lock', title: t('editAssign.title'), text: t('editAssign.noRights') })); return; }
    if (d.result) st.appendChild(E.ResultBanner({ id: 'doc-ea-result', title: d.result.title, text: d.result.text, actions: d.result.actions || [], onDismiss: function () { d.result = null; } }));
    var items = sortedItems();
    if (!items.length) { st.appendChild(E.EmptyState({ icon: 'users', title: t('editAssign.none.title'), text: t('editAssign.none.text'), actions: [{ label: t('editAssign.assign'), variant: 'primary', id: 'doc-ea-empty-assign', onClick: function () { openWizardAddressed(); } }] })); return; }
    st.appendChild(E.Segmented({ id: 'doc-ea-view', label: t('editAssign.view'), value: d.view, options: [{ value: 'months', label: t('editAssign.months') }, { value: 'people', label: t('editAssign.people') }], onChange: function (v) { d.view = v; ctx.setQuery({ view: v === 'people' ? 'people' : null }); paintDrawer(); var s = document.getElementById('doc-ea-view'); if (s) { var bt = s.querySelector('[aria-pressed="true"]'); if (bt) bt.focus(); } } }));
    var live = h('div', { id: 'doc-ea-live', class: 'doc-ea-live', 'aria-live': 'polite' }, d.view === 'people' ? peopleTable() : monthsTable()); st.appendChild(live);
    if (d.view === 'months') st.appendChild(legend());
  }
  function openDrawer(arg) {
    if (drawer && drawer.ctl && document.contains(drawer.ctl.el)) { paintDrawer(); return; }
    var d = drawer = { view: ctx.query.view === 'people' ? 'people' : 'months', result: (arg && arg.result) || null, ctl: null }, mgr = isManager();
    d.ctl = E.Drawer({ id: 'doc-ea-panel', wide: true, title: t('editAssign.title'), subtitle: t('editAssign.owner', { name: isManager() ? E.me().name : host.doc().owner }), body: function () { },
      footer: mgr ? [{ label: t('editAssign.manageSets'), icon: 'users', align: 'left', keepOpen: true, id: 'doc-ea-sets', onClick: function () { ctx.openDialog('editor-set'); } },
        { label: t('editAssign.repeatNext'), icon: 'copy', keepOpen: true, id: 'doc-ea-repeat', onClick: function () { var l = sortedItems(); if (l.length) ctx.openDialog('assign-repeat'); else E.Toast(t('editAssign.none.title')); } },
        { label: t('editAssign.assign'), icon: 'plus', variant: 'primary', keepOpen: true, id: 'doc-ea-assign', onClick: function () { ctx.openDialog('assign-wizard'); } }] : [{ label: 'Close' }],
      onClose: function () { drawer = null; } });
    paintDrawer();
  }
  function showResult(res) {
    if (drawer && drawer.ctl && document.contains(drawer.ctl.el)) { drawer.result = res; paintDrawer(); return; }
    drawer = null; ctx.setQuery({ dialog: null }); setTimeout(function () { ctx.openPanel('edit-assignments'); setTimeout(function () { if (drawer) { drawer.result = res; paintDrawer(); } }, 0); }, 0);
  }
  function afterChange() { host.rerender(); paintDrawer(); paintBadge(); }

  /* ================= майстер передачі: що → кому → підсумок ================= */
  function openWizardAddressed() { ctx.openDialog('assign-wizard'); }
  function defaultPre() { var p = host.period(); if (!openPeriod(p)) p = D.currentPeriod; return { periods: [p], users: [], allowOthers: false, note: '', sheet: '', setName: '' }; }
  function openWizard(pre, o) {
    o = o || {}; var base = defaultPre(), data = Object.assign({}, base, { setId: '', saveSet: false, newSetName: '' }, pre || {});
    data.periods = data.periods.slice(); data.users = data.users.slice();
    function plist() { return monthsText(data.periods); }
    var api = E.Wizard({ id: 'doc-ea-wiz', title: t('editAssign.wizard.title'), data: data, applyLabel: t('editAssign.assign').replace('…', ''), reviewLabel: t('editAssign.wizard.summary'), reviewText: t('editAssign.wizard.reviewText'), steps: [
      { id: 'what', label: t('editAssign.wizard.what'), canNext: function (d) { return d.periods.length > 0; }, validate: function (d) { return d.periods.length ? null : t('editAssign.err.noMonths'); }, render: function (body, d, a) {
        body.appendChild(h('p', { class: 'muted' }, t('editAssign.hint')));
        var grid = h('div', { class: 'doc-ea-chips', role: 'group', 'aria-label': t('editAssign.wizard.months') });
        D.periods.forEach(function (p) { var closed = !openPeriod(p.id), on = d.periods.indexOf(p.id) >= 0;
          grid.appendChild(E.Button({ label: monthName(p.id), id: 'doc-ea-m-' + p.id.slice(5), pressed: on, disabled: closed, title: closed ? t('editAssign.wizard.closedMonth') : null, onClick: function (e, btn) {
            var i = d.periods.indexOf(p.id); if (i >= 0) d.periods.splice(i, 1); else d.periods.push(p.id); btn.setAttribute('aria-pressed', String(i < 0)); a.refreshButtons(); } })); });
        body.appendChild(grid);
        var sheets = [{ value: '', label: t('editAssign.wizard.allSheets') }].concat(host.doc().sheets.map(function (s) { return { value: s.name, label: s.name }; }));
        body.appendChild(E.Collapsible({ id: 'doc-ea-sheet-c', title: t('editAssign.wizard.onlySheet'), summary: d.sheet || t('editAssign.wizard.allSheets'), open: !!d.sheet, content: function (c) { c.appendChild(E.Select({ id: 'doc-ea-sheet', label: t('editAssign.wizard.onlySheet'), options: sheets, value: d.sheet, onChange: function (v) { d.sheet = v; } })); } }));
      } },
      { id: 'who', label: t('editAssign.wizard.who'), validate: function (d) { if (!d.users.length) return t('editAssign.err.noExecutors'); if (d.saveSet && !d.newSetName.trim()) return t('editAssign.err.setName'); return null; }, render: function (body, d) {
        var chosen = h('div', { class: 'doc-ea-chosen', id: 'doc-ea-chosen', 'aria-live': 'polite' }), results = h('div', { class: 'doc-ea-results', id: 'doc-ea-results', 'aria-live': 'polite' }), setBox = h('div', { class: 'stack gap-2', hidden: !d.saveSet });
        function paintChosen() { chosen.textContent = ''; if (!d.users.length) chosen.appendChild(h('span', { class: 'muted' }, '—'));
          d.users.forEach(function (u) { chosen.appendChild(h('span', { class: 'doc-ea-pill' }, h('span', {}, u), visBadge(u), E.Button({ iconOnly: true, icon: 'x', label: t('editAssign.wizard.remove', { name: u }), onClick: function () { d.users.splice(d.users.indexOf(u), 1); d.setId = ''; paintChosen(); search(); } }))); }); }
        function search() { var q = (qi.value || '').trim().toLowerCase(); results.textContent = ''; if (q.length < 2) return;
          var m = PEOPLE.filter(function (p) { return p.n.toLowerCase().indexOf(q) >= 0; });
          if (!m.length) results.appendChild(h('span', { class: 'muted' }, t('editAssign.wizard.noMatch', { q: qi.value.trim() })));
          m.forEach(function (p) { var has = d.users.indexOf(p.n) >= 0; results.appendChild(h('button', { type: 'button', class: 'doc-ea-pick', disabled: has, id: 'doc-ea-pick-' + p.n.replace(/\W+/g, '').toLowerCase(), on: { click: function () { d.users.push(p.n); d.setId = ''; paintChosen(); search(); } } }, h('span', {}, p.n), h('span', { class: 'doc-ea-vis' }, visText(p.n)))); }); }
        var setSel = E.Select({ id: 'doc-ea-set', label: t('editAssign.wizard.set'), options: [{ value: '', label: t('editAssign.wizard.noSet') }].concat(SETS.map(function (s) { return { value: s.id, label: s.name + ' · ' + s.users.length }; })), value: d.setId, onChange: function (v) {
          var s = SETS.filter(function (x) { return x.id === v; })[0]; d.setId = v; if (s) { d.users = s.users.slice(); d.setName = s.name; } else d.setName = ''; paintChosen(); search(); } });
        var qf = E.Input({ id: 'doc-ea-search', label: t('editAssign.wizard.search'), hint: t('editAssign.wizard.searchHint'), type: 'search', icon: 'search', onInput: function () { search(); } }), qi = qf.input;
        var nameF = E.Input({ id: 'doc-ea-setname', label: t('editAssign.wizard.setName'), hint: t('editAssign.wizard.setHint'), value: d.newSetName, onInput: function (v) { d.newSetName = v; } });
        setBox.appendChild(nameF);
        var save = E.Checkbox({ id: 'doc-ea-saveset', label: t('editAssign.wizard.saveSet'), checked: d.saveSet, onChange: function (on) { d.saveSet = on; setBox.hidden = !on; } });
        var note = E.Textarea({ id: 'doc-ea-note', label: t('editAssign.note'), rows: 2, maxlength: 300, onInput: function (v) { d.note = v; } }); note.input.value = d.note || '';
        E.append(body, [setSel, qf, results, h('div', { class: 'stack gap-1' }, h('b', { class: 't-sb' }, t('editAssign.wizard.chosen')), chosen), save, setBox, note]);
        paintChosen();
      } }],
      summary: function (d) {
        var months = plist(), hidden = d.users.filter(function (u) { return visOf(u) === 'NotVisible'; }).length, rule = h('p', { id: 'doc-ea-sum-rule' });
        function paintRule() { rule.textContent = t(d.allowOthers ? 'editAssign.wizard.sumOpen' : 'editAssign.wizard.sumExclusive', { months: months }); }
        paintRule();
        return h('div', { class: 'stack gap-3', id: 'doc-ea-summary' },
          E.KeyValue([{ label: t('editAssign.wizard.sumMonths'), value: months }, { label: t('editAssign.wizard.sumExecutors'), value: h('div', { class: 'stack gap-1' }, d.users.map(function (u) { return h('div', { class: 'doc-ea-ex' }, h('span', {}, u), visBadge(u) || h('span', { class: 'doc-ea-vis' }, visText(u))); })) },
            d.setName && d.setId ? { label: t('editAssign.wizard.set'), value: '“' + d.setName + '”' } : d.saveSet ? { label: t('editAssign.wizard.setName'), value: '“' + d.newSetName.trim() + '”' } : null, d.sheet ? { label: t('editAssign.wizard.onlySheet'), value: d.sheet } : null, d.note ? { label: t('editAssign.note'), value: d.note } : null].filter(Boolean)),
          h('p', {}, t('editAssign.wizard.sumEffect')), rule,
          E.Checkbox({ id: 'doc-ea-allow-others', label: t('editAssign.exclusive'), hint: t('editAssign.exclusive.hint'), checked: d.allowOthers, onChange: function (on) { d.allowOthers = on; paintRule(); } }),
          hidden ? E.Banner({ tone: 'warning', id: 'doc-ea-sum-vis', text: t('editAssign.wizard.noVisible', { n: hidden }) }) : null);
      },
      onApply: function (d, a) {
        if (sim422) { var sheets = ['“GHG inventory”', '“Energy”']; if (d.sheet) sheets = ['“' + d.sheet + '”']; a.fail('ECR-REQ-0422 · delegationExceedsOwnRights — ' + t('editAssign.err.exceeds', { sheets: sheets.join(', ') })); return; }
        var months = plist(), touched = applyWizard(host.docId(), d); a.close(); paintBadge(); host.rerender();
        showResult({ title: t('editAssign.result.assigned', { months: months }), text: t('editAssign.result.assignedText'), actions: [{ label: t('editAssign.repeatNext'), onClick: function () { repeatNext(touched[touched.length - 1]); } }] });
      } });
    if (o.startAt === 'who' || o.startAt === 'review') api.next(); if (o.startAt === 'review') api.next();
    return api;
  }
  function repeatNext(it) {
    var np = nextPeriod(it.period); if (!np) { E.Toast(t('editAssign.err.noNext'), 'warning'); return; } if (!openPeriod(np)) { E.Toast(t('editAssign.err.nextClosed'), 'warning'); return; }
    ctx.setQuery({ dialog: null }); openWizard({ periods: [np], users: it.recipients.map(function (r) { return r.u; }), allowOthers: !it.exclusive, note: it.note, sheet: it.sheet, setName: it.setName }, { startAt: 'review' });
  }

  /* ================= діалоги: валідувати · повернути · зняти · набори ================= */
  function target(arg) { var docId = host.docId(); return (arg && byId(docId, arg)) || find(docId, host.period()); }
  function dlgValidate(arg) {
    var it = target(arg); if (!it) { it = seedAwaiting(); } var mon = monthName(it.period);
    if (status(it) !== 'Filled') { var left = it.recipients.filter(function (r) { return r.st !== 'Filled'; }).map(function (r) { return r.u; });
      E.Dialog({ id: 'doc-ea-validate-blocked', title: t('editAssign.validate.blocked'), body: function (b) { b.appendChild(E.Banner({ tone: 'warning', id: 'doc-ea-blocked', title: 'ECR-REQ-0422 · notAllFilled', text: t('editAssign.validate.blockedText', { names: left.join(', ') || '—', month: mon }) })); }, footer: [{ label: 'Close', autofocus: true }] }); return; }
    E.ConfirmDialog({ id: 'doc-ea-validate', title: t('editAssign.validate.title', { month: mon }), consequences: [t('editAssign.validate.c1', { month: mon }), t('editAssign.validate.c2', { month: mon }), { text: t('editAssign.validate.c3'), note: true }], verb: t('editAssign.validate.verb'), icon: 'checkCircle',
      onConfirm: function () { doValidate(it); afterChange(); setTimeout(function () { showResult({ title: t('editAssign.result.validated', { month: mon }), text: t('editAssign.result.validatedText', { month: mon }) }); }, 0); } });
  }
  function seedAwaiting() { applyScenario(host.docId(), 'awaiting'); host.rerender(); return find(host.docId(), '2026-09'); }
  function dlgReturn(arg) {
    var it = target(arg); if (!it || !it.recipients.some(function (r) { return r.st === 'Filled'; })) { it = seedAwaiting(); } var mon = monthName(it.period), picks = {};
    var filled = it.recipients.filter(function (r) { return r.st === 'Filled'; }); filled.forEach(function (r) { picks[r.u] = true; });
    var extra = h('fieldset', { class: 'stack gap-1', id: 'doc-ea-ret-who' }, h('legend', { class: 't-sb' }, t('editAssign.return.to')), filled.map(function (r, i) {
      return E.Checkbox({ id: 'doc-ea-ret-' + i, label: r.u, checked: true, onChange: function (on) { picks[r.u] = on; var ok = $('doc-ea-ret-ok'), tx = $('doc-ea-ret-text'); if (ok && tx) ok.disabled = !tx.value.trim() || !Object.keys(picks).some(function (k) { return picks[k]; }); } }); }));
    E.ReasonDialog({ id: 'doc-ea-ret', title: t('editAssign.return.title', { month: mon }), label: t('editAssign.return.label'), hint: t('editAssign.return.hint'), minLength: 1, verb: t('editAssign.return.verb'), icon: 'return', extra: extra,
      onSubmit: function (reason) { var users = Object.keys(picks).filter(function (k) { return picks[k]; }); if (!users.length) return; doReturn(it, users, reason); afterChange(); setTimeout(function () { showResult({ title: t('editAssign.result.returned', { month: mon }), text: t('editAssign.result.returnedText') }); }, 0); } });
  }
  function $(id) { return document.getElementById(id); }
  function dlgRemove(arg) {
    var it = target(arg); if (!it) return; var mon = monthName(it.period);
    E.ConfirmDialog({ id: 'doc-ea-remove', title: t('editAssign.remove.title', { month: mon }), consequences: [t('editAssign.remove.c1', { month: mon }), t('editAssign.remove.c2'), { text: t('editAssign.remove.c3'), note: true }], verb: t('editAssign.remove.verb'), icon: 'trash', danger: true,
      onConfirm: function () { var l = itemsOf(host.docId()); l.splice(l.indexOf(it), 1); afterChange(); E.Toast(t('editAssign.toast.removed', { month: mon })); } });
  }
  function dlgSets() {
    var ctl = E.Dialog({ id: 'doc-ea-sets-dlg', wide: true, title: t('editorSet.title'), body: function (b) { paintSets(b); }, footer: [{ label: 'Close', autofocus: true }] });
    function paintSets(b) {
      b.textContent = ''; b.appendChild(h('p', { class: 'muted' }, t('editorSet.hint')));
      if (!SETS.length) b.appendChild(E.EmptyState({ icon: 'users', title: t('editorSet.title'), text: t('editorSet.empty') }));
      else b.appendChild(h('div', { class: 'doc-ea-wrap' }, h('table', { class: 'data doc-ea-sets' }, h('caption', { class: 'sr' }, t('editorSet.title')), h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, t('editorSet.title')), h('th', { scope: 'col' }, t('editorSet.members')), h('th', { scope: 'col' }, h('span', { class: 'sr' }, t('editAssign.col.actions'))))),
        h('tbody', {}, SETS.map(function (s) { return h('tr', {}, h('th', { scope: 'row' }, s.name), h('td', { class: 'doc-ea-ex-cell' }, s.users.join(', ')), h('td', {}, E.Button({ iconOnly: true, icon: 'trash', label: t('editorSet.delete') + ': ' + s.name, id: 'doc-ea-setdel-' + s.id, onClick: function (e, btn) { E.InlineConfirm(btn, { text: t('editorSet.deleteAsk', { name: s.name }), verb: t('editorSet.delete'), danger: true, onConfirm: function () { SETS.splice(SETS.indexOf(s), 1); paintSets(b); } }); } }))); })))));
      var members = {}, nameF = E.Input({ id: 'doc-ea-newset-name', label: t('editAssign.wizard.setName'), required: true }), row = h('div', { class: 'doc-ea-pickrow', role: 'group', 'aria-label': t('editorSet.members') }, PEOPLE.map(function (p, i) { return E.Checkbox({ id: 'doc-ea-newset-' + i, label: p.n, onChange: function (on) { members[p.n] = on; } }); })), err = h('div', { class: 'hint bad', role: 'alert', hidden: true });
      b.appendChild(h('div', { class: 'stack gap-2' }, h('b', { class: 't-sb' }, t('editorSet.new')), nameF, row, err, h('div', {}, E.Button({ label: t('editorSet.save'), icon: 'save', id: 'doc-ea-newset-save', onClick: function () {
        var users = Object.keys(members).filter(function (k) { return members[k]; }); nameF.setError(null); err.hidden = true;
        if (!nameF.value.trim()) { nameF.setError(t('editAssign.err.setName')); nameF.input.focus(); return; } if (!users.length) { err.textContent = t('editorSet.err.members'); err.hidden = false; return; }
        SETS.push({ id: 'set-' + (SETS.length + 1), name: nameF.value.trim(), users: users }); paintSets(b); } }))));
    }
  }

  /* ================= «My tasks» і позначка на кнопці шапки (рішення макета M0: окрема крапка, не змішана з фоновими задачами) ================= */
  function allDocs() { return D.documents.map(function (d) { return d.id; }); }
  function myAssignments() { var out = []; allDocs().forEach(function (id) { itemsOf(id).forEach(function (it) { var r = mine(it); if (r && !it.validated && r.st !== 'Filled') out.push({ docId: id, it: it, r: r }); }); }); return out; }
  function myValidations() { var out = []; if (!isManager()) return out; allDocs().forEach(function (id) { awaitingList(id).forEach(function (it) { out.push({ docId: id, it: it }); }); }); return out; }
  function taskRow(o, kind) {
    var mon = monthName(o.it.period), href = '#/documents/' + o.docId + '?period=' + o.it.period, doc = D.doc(o.docId);
    return h('div', { class: 'task doc-ea-task', id: 'doc-ea-task-' + kind + '-' + o.docId + '-' + o.it.period },
      h('div', { class: 'task-top' }, icon(kind === 'mine' ? 'pencil' : 'checkCircle'), h('b', { title: o.docId + ' · ' + mon }, mon + ' · ' + o.docId), kind === 'mine' ? recipBadge(o.r, false) : sliceBadge('Filled')),
      h('span', { class: 'muted t-xs' }, [doc ? doc.facility : '', o.it.note].filter(Boolean).join(' · ')),
      kind === 'mine' && o.r.st === 'Returned' ? h('blockquote', { class: 'doc-ea-quote t-xs', 'aria-label': t('myTasks.returnComment') }, '“' + o.r.reason + '”') : null,
      h('div', { class: 'doc-ea-acts' }, h('a', { class: 'btn btn-sm', href: href, id: 'doc-ea-task-open-' + kind + '-' + o.docId }, t('myTasks.open')), kind === 'val' ? h('a', { class: 'btn btn-sm', href: href + '&dialog=assign-validate' }, t('editAssign.validate')) : null));
  }
  function taskSections() {
    var a = myAssignments(), v = myValidations(); if (!a.length && !v.length) return null; var box = h('div', { id: 'doc-ea-tasks' });
    [[a, 'mine', 'myTasks.assignToFill'], [v, 'val', 'myTasks.awaitingValidation']].forEach(function (s) { if (!s[0].length) return;
      box.appendChild(h('div', { class: 'doc-ea-sech' }, h('h3', { class: 'eyebrow' }, t(s[2])), h('span', { class: 'count' }, s[0].length))); s[0].forEach(function (o) { box.appendChild(taskRow(o, s[1])); }); });
    return box;
  }
  function paintBadge() {
    var btn = document.getElementById('btn-tasks'); if (!btn) return; var n = myAssignments().length + myValidations().length, c = document.getElementById('doc-ea-tb-count');
    if (!c) { c = h('span', { class: 'count', id: 'doc-ea-tb-count', hidden: true }); btn.appendChild(c); }
    c.textContent = n; c.hidden = !n; c.title = n + ' · ' + t('myTasks.assignToFill') + ' / ' + t('myTasks.awaitingValidation'); c.setAttribute('aria-label', c.title);
    if (E.ea_repaintTasks) E.ea_repaintTasks();
  }
  E.taskSections = (E.taskSections || []).concat([taskSections]); // вставляє edit-assignments/kit-ext.js
  window.addEventListener('hashchange', function () { setTimeout(paintBadge, 0); });
  document.addEventListener('click', function () { setTimeout(paintBadge, 0); }, true);

  /* ================= реєстрація з екрана документа ================= */
  function register(c, hst) {
    ctx = c; host = hst; var q = c.query; LANG = I18N[q.lang] ? q.lang : 'en'; sim422 = q.ea422 === '1' || sim422 && q.ea422 !== '0';
    if (q.ea) applyScenario(host.docId(), q.ea);
    c.panel('edit-assignments', function () { openDrawer(); });
    c.dialog('assign-wizard', function () { // ?step=who|review відкривають майстер одразу на кроці; без set — демо-набір «Dept 5»
      var s = SETS.filter(function (x) { return x.id === (q.set || (q.step ? 'set-1' : '')); })[0], pre = s ? { users: s.users.slice(), setName: s.name, setId: q.set ? s.id : '' } : {};
      if (q.nv && pre.users) pre.users.push('G. Tulegenova', 'O. Kovalenko'); // демо: люди без видимості документа
      openWizard(pre, { startAt: q.step }); });
    c.dialog('assign-repeat', function () { var l = sortedItems(); if (!l.length) return; repeatNext(l[l.length - 1]); });
    c.dialog('assign-validate', dlgValidate); c.dialog('assign-return', dlgReturn); c.dialog('assign-remove', dlgRemove); c.dialog('editor-set', dlgSets);
    c.dialog('assign-fill-confirm', function () { setTimeout(function () { var b = $('doc-ea-mark-filled'); if (b) askFill(b); }, 0); });
    c.dialog('result-assigned', function () { showResult({ title: t('editAssign.result.assigned', { months: monthName(host.period()) }), text: t('editAssign.result.assignedText') }); });
    c.dialog('result-validated', function () { var it = target(); if (!it || status(it) !== 'Filled' && !it.validated) it = seedAwaiting(); doValidate(it); host.rerender(); showResult({ title: t('editAssign.result.validated', { month: monthName(it.period) }), text: t('editAssign.result.validatedText', { month: monthName(it.period) }) }); });
    c.dialog('result-returned', function () { var it = target(); if (!it || !it.recipients.some(function (r) { return r.st === 'Filled'; })) it = seedAwaiting(); doReturn(it, it.recipients.filter(function (r) { return r.st === 'Filled'; }).map(function (r) { return r.u; }), COMMENT); host.rerender(); showResult({ title: t('editAssign.result.returned', { month: monthName(it.period) }), text: t('editAssign.result.returnedText') }); });
    c.proto({ id: 'ea-scenario', label: 'Handover of September (demo)', options: SCENARIOS, value: q.ea && SCENARIOS.indexOf(q.ea) >= 0 ? q.ea : 'mixed', onChange: function (v) { applyScenario(host.docId(), v); host.rerender(); paintDrawer(); paintBadge(); } });
    c.proto({ id: 'ea-server', label: 'Server answer to “Assign”', buttons: [{ label: 'OK', onClick: function () { sim422 = false; E.Toast('Assign succeeds'); } }, { label: '422 exceeds own rights', onClick: function () { sim422 = true; E.Toast('Assign fails with delegationExceedsOwnRights', 'warning'); } }] });
    c.action(t('editAssign.title') + '…', 'users', function () { c.openPanel('edit-assignments'); }); c.action(t('editAssign.assign'), 'plus', function () { c.openDialog('assign-wizard'); });
    paintBadge();
  }
  E.ea = { register: register, lockText: lockText, paintBar: paintBar, moreItems: moreItems, paintBadge: paintBadge, t: t, KEYS: KEYS };

  /* ================= flows (карта переходів — /flows) ================= */
  var D1 = '#/documents/DOC-000001', G = 'Reporting — edit access';
  E.flow('ea-owner-assign', { group: G, title: 'Hand a month to colleagues', actor: 'Document owner · project manager', note: 'Document → More → Edit access → 3-step wizard. Step 3 shows who can open the document (Visible / NotVisible) and the “also allow others” tick.', steps: [
    { label: 'Open the document as owner', href: D1 + '?as=Approver' }, { label: 'More → Edit access (months)', href: D1 + '?as=Approver&panel=edit-assignments' }, { label: 'Step 1 · which months', href: D1 + '?as=Approver&dialog=assign-wizard' },
    { label: 'Step 2 · to whom (a saved set)', href: D1 + '?as=Approver&dialog=assign-wizard&step=who&set=set-1' }, { label: 'Step 3 · summary + visibility', href: D1 + '?as=Approver&dialog=assign-wizard&step=review' },
    { label: 'Step 3 · someone cannot open the document', href: D1 + '?as=Approver&dialog=assign-wizard&step=review&nv=1' }, { label: 'Server refuses: delegationExceedsOwnRights', href: D1 + '?as=Approver&dialog=assign-wizard&step=review&ea422=1' }, { label: 'Result · what next', href: D1 + '?as=Approver&dialog=result-assigned' }] });
  E.flow('ea-views-sets', { group: G, title: 'Months × executors × status; sets; repeat for next month', actor: 'Document owner', steps: [
    { label: 'View “Months”', href: D1 + '?as=Approver&panel=edit-assignments' }, { label: 'View “People”', href: D1 + '?as=Approver&panel=edit-assignments&view=people' }, { label: 'Manage sets · save a new set', href: D1 + '?as=Approver&panel=edit-assignments&dialog=editor-set' },
    { label: 'Repeat for next month · summary prefilled', href: D1 + '?as=Approver&dialog=assign-repeat' }, { label: 'Remove a handover (consequences)', href: D1 + '?as=Approver&panel=edit-assignments&dialog=assign-remove' }, { label: 'Nothing handed over yet', href: D1 + '?as=Approver&ea=empty&panel=edit-assignments' }] });
  E.flow('ea-executor-fill', { group: G, title: 'Executor fills the month and marks it as filled', actor: 'Executor (Data entry)', note: 'After “Filled” the cells turn into the existing hatched grey with a tooltip; select a cell to read the reason in the formula bar.', steps: [
    { label: 'Strip: “September was handed to you”', href: D1 + '?as=DataEntry&ea=assigned' }, { label: 'Mark as filled · inline confirm', href: D1 + '?as=DataEntry&ea=assigned&dialog=assign-fill-confirm' },
    { label: 'Filled · cells locked (AssignmentFilled)', href: D1 + '?as=DataEntry&ea=filled' }, { label: 'Validated · cells locked (AssignmentValidated)', href: D1 + '?as=DataEntry&ea=validated' }, { label: 'Another month, handed to others (LockedByOwner)', href: D1 + '?as=DataEntry&ea=other' }] });
  E.flow('ea-owner-validate', { group: G, title: 'Owner validates or returns with a comment', actor: 'Document owner', steps: [
    { label: 'Strip: filled — awaiting validation', href: D1 + '?as=Approver&ea=awaiting' }, { label: 'Validate · consequences', href: D1 + '?as=Approver&ea=awaiting&dialog=assign-validate' }, { label: 'Not everyone filled (422 notAllFilled)', href: D1 + '?as=Approver&ea=filled&dialog=assign-validate' },
    { label: 'Result: validated', href: D1 + '?as=Approver&ea=awaiting&dialog=result-validated' }, { label: 'Return with a comment · required 1–500', href: D1 + '?as=Approver&ea=awaiting&dialog=assign-return' }, { label: 'Result: returned', href: D1 + '?as=Approver&ea=awaiting&dialog=result-returned' },
    { label: 'Executor sees the comment', href: D1 + '?as=DataEntry&ea=returned' }] });
  E.flow('ea-my-tasks', { group: G, title: 'My tasks: Assigned to me · Awaiting my validation', actor: 'Executor · owner', steps: [
    { label: 'Executor (with a returned month)', href: D1 + '?as=DataEntry&ea=returned&panel=tasks' }, { label: 'Owner (months wait for validation)', href: D1 + '?as=Approver&ea=awaiting&panel=tasks' }, { label: 'Executor sees the owner’s side as read-only', href: D1 + '?as=DataEntry&panel=edit-assignments' }] });
  E.flow('ea-i18n', { group: G, title: 'Languages', actor: 'Any', steps: [{ label: 'Russian', href: D1 + '?as=Approver&lang=ru&panel=edit-assignments' }, { label: 'Kazakh', href: D1 + '?as=Approver&lang=kz&ea=awaiting' }, { label: 'Dark theme', href: D1 + '?as=Approver&ea=awaiting&panel=edit-assignments&theme=dark' }] });
})(window.ECR);
