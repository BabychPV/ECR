# EA-0 · макет «Доступ до редагування» (передача місяця третій стороні)

Макет функції з `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §8 (рішення людини 11.10). Це **частина екрана документа**, окремого
маршруту немає. Запуск: `node docs/design/hybrid/serve.js` → `http://127.0.0.1:5197/#/flows` → група **Reporting — edit access**.
Код: `edit-assignments/screen-edit-assignments.js` (підключається з `screen-document.js` через `ECR.ea`; `KIT.md` не змінено).

## Що показано

| # | Екран / стан | Адреса (після `#/documents/DOC-000001`) |
|---|---|---|
| 1 | Шторка «Edit access» (More → Edit access): матриця «місяць × виконавець × статус», вид **Months** | `?as=Approver&panel=edit-assignments` |
| 1 | Той самий, вид **People** (виконавець × Jan…Dec, форма маркера + прихований текст + легенда) | `…&view=people` |
| 1 | Майстер передачі, 3 кроки: **Що** (чіпси місяців, «Only sheet» у згорнутій секції) → **Кому** (збережений набір АБО пошук ≥ 2 символів, «Save as set», примітка) → **Підсумок** (видимість Visible/NotVisible/Unknown, галочка «Also allow others with role rights») | `?as=Approver&dialog=assign-wizard` (+ `step=who&set=set-1`, `step=review`, `nv=1`) |
| 1 | Відмова сервера `ECR-REQ-0422 delegationExceedsOwnRights` на кроці 3 | `…&step=review&ea422=1` |
| 1 | «Repeat for next month» — майстер одразу на підсумку з тими самими людьми | `?as=Approver&dialog=assign-repeat` |
| 1 | «Manage sets» (список, видалення з InlineConfirm, новий набір) | `?as=Approver&panel=edit-assignments&dialog=editor-set` |
| 2 | Смуга виконавця над таблицею: «September was handed to you» + «Mark as filled» (InlineConfirm) | `?as=DataEntry&ea=assigned` · `…&dialog=assign-fill-confirm` |
| 2 | Після «Filled»: смуга без кнопок, комірки — наявна штрихована сіра з підказкою `deny.AssignmentFilled` | `?as=DataEntry&ea=filled` |
| 2 | Повернено з коментарем (текст коментаря в смузі) | `?as=DataEntry&ea=returned` |
| 3 | Власник: смуга «filled — awaiting your validation», «Validate…» / «Return with comment…» | `?as=Approver&ea=awaiting` |
| 3 | Діалог повернення: коментар **обов'язковий, 1–500 символів**, вибір виконавців | `…&dialog=assign-return` |
| 3 | Діалог валідації з наслідками; відмова `notAllFilled` | `…&dialog=assign-validate` · `?ea=filled&dialog=assign-validate` |
| 4 | «My tasks»: секції **Assigned to me** (із коментарем повернення) і **Awaiting my validation**; окрема цифра біля кнопки шапки | `?as=DataEntry&ea=returned&panel=tasks` · `?as=Approver&ea=awaiting&panel=tasks` |
| 5 | Стан комірки: «зафіксовано власником» (`deny.LockedByOwner`, місяць передано іншим), «заповнено» (`deny.AssignmentFilled`), «валідовано» (`deny.AssignmentValidated`) | `?as=DataEntry&ea=other` · `ea=filled` · `ea=validated` |
| 6 | Мови en/ru/kz | `?lang=ru`, `?lang=kz` |

Демо-стан вересня DOC-000001 перемикається параметром `ea=` (`mixed` за замовчуванням, `assigned`, `filled`, `awaiting`, `returned`,
`validated`, `other`, `none`, `empty`) або в панелі «Prototype». Власник/менеджер = ролі Approver/Admin, виконавець = Data entry
(D. Akhmetova); у продукті це «автор документа або `Manage` на проєкт» (дизайн §2.4).

## Рішення макета (дефолти, змінюються без переробки)

- **Жодного нового кольору й нового бейджа.** Статус виконавця/зрізу = `StatusBadge('sheet', …, {label})`: Assigned → нейтральний, Filled → **info** («чекає дії»),
  Returned → **warn**, Validated → нейтральний ✓. Стан комірки використовує наявну `is-locked` (штрихування + замок) і підказку.
  «Немає доступу до документа» = `StatusBadge('health','Degraded',{label})` (warn). Статус ніколи не лише кольором: завжди текст, у «People» — ще форма точки + прихований текст.
- Кнопка «Validate…» у смузі власника — не `primary` (на екрані вже є «Submit»).
- Підсумок після дії — `ResultBanner` із «що далі» (текст про наступний крок, а не «все добре»).
- Позначка на «My tasks» — окрема цифра (`#doc-ea-tb-count`), а не змішана з лічильником фонових задач (M0).
- Ознака «Others can't edit…» формулюється без імен (R9); текст повернення бачить лише повернутий виконавець і менеджери.
- «Change executors…» у меню рядка відкриває той самий майстер із наперед заповненими даними; вибір існуючого місяця замінює склад (стани наявних виконавців зберігаються).

## Відхилення від наявного набору (для звіту, не виправлялось)

1. **`ECR.css`-префікс `doc-ea`** (поруч із `doc`); селектори лише `.doc-ea-…`, жодних глобальних.
2. **`kit.js`, `index.html`, `KIT.md` не змінено.** Розширення — у `edit-assignments/`: `kit-ext.js` (вставляє секції в шторку «My tasks» через обгортку `E.Drawer`) і `screen-edit-assignments.js`. Обидва підключає `screen-document.js` одним `document.write` угорі файлу.
3. **`screen-document.js`** — 5 хуків `E.ea.*` (More, смуга `#doc-ea-bar`, `lockText`, `cellCls`, `register`) і рядок підключення.
4. Кнопки набору («Next», «Back», «Cancel», «Close») залишаються англійськими: вони з `kit.js`, у застосунку — наявні ключі. Назви місяців — зі списку (`MN`), у застосунку — наявні ключі місяців.
5. Позначка замка в заголовку колонки (дизайн §8) не малювалась: у макеті права комірок не рахуються по колонках.
6. Esc у майстрі закриває його одразу (як `E.Wizard` набору): `UnsavedGuard` на вихід із незбереженого майстра з дизайну §8 у наборі не передбачено — питання до EA-5.

## Ключі i18n (секція `-- EDA:` у `09-seed.sql`; en · ru · kz)

Користувацькі тексти не пишуться літералами — лише ключі. Нижче — ті, що використовує макет (інші з дизайну §8, як-от `err.ECR-*`, `audit.event.*`, `notify.edit.*`, додаються серверними лейнами).

| ключ | en | ru | kz |
|---|---|---|---|
| `deny.LockedByOwner` | Locked: this month is handed to other users | Заблокировано: месяц передан другим пользователям | Бұғатталған: ай басқа пайдаланушыларға берілген |
| `deny.AssignmentFilled` | Locked: you marked this month as filled. The owner can return it for edits. | Заблокировано: вы отметили месяц как заполненный. Владелец может вернуть его на доработку. | Бұғатталған: сіз айды толтырылған деп белгіледіңіз. Иесі оны қайта өңдеуге қайтара алады. |
| `deny.AssignmentValidated` | Locked: the owner validated this month | Заблокировано: владелец валидировал этот месяц | Бұғатталған: иесі бұл айды валидациялады |
| `editAssign.title` | Edit access | Доступ к редактированию | Өңдеу қолжетімділігі |
| `editAssign.hint` | Hand a month to someone to fill in, then validate it. | Передайте месяц исполнителю на заполнение, затем валидируйте его. | Айды толтыру үшін орындаушыға беріңіз, содан кейін валидациялаңыз. |
| `editAssign.owner` | Owner: {name} | Владелец: {name} | Иесі: {name} |
| `editAssign.noRights` | Only the document owner or a project manager can manage Edit access. | Доступом к редактированию управляют только владелец документа или менеджер проекта. | Өңдеу қолжетімділігін тек құжат иесі немесе жоба менеджері басқара алады. |
| `editAssign.view` | View | Вид | Көрініс |
| `editAssign.months` | Months | Месяцы | Айлар |
| `editAssign.people` | People | Люди | Адамдар |
| `editAssign.col.month` | Month | Месяц | Ай |
| `editAssign.col.executors` | Executors | Исполнители | Орындаушылар |
| `editAssign.col.status` | Status | Статус | Күйі |
| `editAssign.col.changed` | Changed | Изменено | Өзгертілді |
| `editAssign.col.actions` | Actions | Действия | Әрекеттер |
| `editAssign.col.person` | Person | Человек | Адам |
| `editAssign.status.open` | Owner only | Только владелец | Тек иесі |
| `editAssign.status.assigned` | Assigned | Передано | Берілді |
| `editAssign.status.inProgress` | In progress | В работе | Орындалуда |
| `editAssign.status.filled` | Awaiting validation | Ждёт валидации | Валидацияны күтуде |
| `editAssign.status.returned` | Returned | Возвращено | Қайтарылды |
| `editAssign.status.validated` | Validated | Валидировано | Валидацияланды |
| `editAssign.recipient.assigned` | Assigned | Передано | Берілді |
| `editAssign.recipient.filled` | Filled | Заполнено | Толтырылды |
| `editAssign.recipient.returned` | Returned | Возвращено | Қайтарылды |
| `editAssign.legend` | Legend | Обозначения | Белгілер |
| `editAssign.assign` | Assign month… | Передать месяц… | Айды беру… |
| `editAssign.repeatNext` | Repeat for next month | Повторить на следующий месяц | Келесі айға қайталау |
| `editAssign.manageSets` | Manage sets | Наборы | Жинақтарды басқару |
| `editAssign.edit` | Change executors… | Изменить исполнителей… | Орындаушыларды өзгерту… |
| `editAssign.remove` | Remove handover… | Снять передачу… | Беруді алып тастау… |
| `editAssign.validate` | Validate… | Валидировать… | Валидациялау… |
| `editAssign.return` | Return with comment… | Вернуть с комментарием… | Пікірмен қайтару… |
| `editAssign.awaiting` | Awaiting validation: {n} | Ждут валидации: {n} | Валидацияны күтуде: {n} |
| `editAssign.open` | Open Edit access | Открыть доступ к редактированию | Өңдеу қолжетімділігін ашу |
| `editAssign.visibility.visible` | Can open the document | Видит документ | Құжатты көреді |
| `editAssign.visibility.notVisible` | No access to the document | Нет доступа к документу | Құжатқа қолжетімділік жоқ |
| `editAssign.visibility.unknown` | Access unknown (AD group) | Доступ неизвестен (группа AD) | Қолжетімділік белгісіз (AD тобы) |
| `editAssign.visibility.hint` | Ask an administrator to give them the role “Executor”. | Попросите администратора выдать роль «Исполнитель». | Әкімшіден «Орындаушы» рөлін беруін сұраңыз. |
| `editAssign.exclusive` | Also allow others with role rights | Разрешить также остальным по ролям | Рөлдері бойынша басқаларға да рұқсат ету |
| `editAssign.exclusive.hint` | Off: until the month is validated or removed, only you and the executors can edit it. | Выключено: пока месяц не валидирован или не снят, править его могут только вы и исполнители. | Өшірулі: ай валидацияланғанша немесе алынғанша оны тек сіз бен орындаушылар өңдей алады. |
| `editAssign.note` | Note for executors | Примечание для исполнителей | Орындаушыларға ескертпе |
| `editAssign.none.title` | No months handed over | Месяцы не передавались | Берілген айлар жоқ |
| `editAssign.none.text` | Hand a month to colleagues to fill in; you validate it afterwards. | Передайте месяц коллегам на заполнение; затем вы его валидируете. | Айды әріптестерге толтыруға беріңіз; кейін өзіңіз валидациялайсыз. |
| `editAssign.wizard.title` | Assign a month | Передача месяца | Айды беру |
| `editAssign.wizard.what` | What | Что | Не |
| `editAssign.wizard.who` | To whom | Кому | Кімге |
| `editAssign.wizard.summary` | Summary | Итог | Қорытынды |
| `editAssign.wizard.reviewText` | Nothing is applied until you confirm. Check the summary below. | Ничего не применится, пока вы не подтвердите. Проверьте итог ниже. | Растағанша ештеңе қолданылмайды. Төмендегі қорытындыны тексеріңіз. |
| `editAssign.wizard.months` | Months to hand over | Передаваемые месяцы | Берілетін айлар |
| `editAssign.wizard.closedMonth` | This period is closed | Период закрыт | Кезең жабық |
| `editAssign.wizard.onlySheet` | Only sheet | Только лист | Тек парақ |
| `editAssign.wizard.allSheets` | All sheets | Все листы | Барлық парақтар |
| `editAssign.wizard.set` | Saved set | Сохранённый набор | Сақталған жинақ |
| `editAssign.wizard.noSet` | — none, pick people below — | — нет, выберите людей ниже — | — жоқ, төменнен адамдарды таңдаңыз — |
| `editAssign.wizard.search` | Find people | Найти людей | Адамдарды іздеу |
| `editAssign.wizard.searchHint` | Type at least 2 characters of the name. | Введите не менее 2 символов имени. | Аттың кемінде 2 таңбасын енгізіңіз. |
| `editAssign.wizard.chosen` | Executors | Исполнители | Орындаушылар |
| `editAssign.wizard.noMatch` | Nobody matches “{q}”. | Никто не найден по «{q}». | «{q}» бойынша ешкім табылмады. |
| `editAssign.wizard.remove` | Remove {name} | Убрать {name} | {name} алып тастау |
| `editAssign.wizard.saveSet` | Save as set | Сохранить как набор | Жинақ ретінде сақтау |
| `editAssign.wizard.setName` | Set name | Название набора | Жинақ атауы |
| `editAssign.wizard.setHint` | Next month you pick this set instead of people. | В следующем месяце вы выберете набор вместо людей. | Келесі айда адамдардың орнына осы жинақты таңдайсыз. |
| `editAssign.wizard.sumMonths` | Months | Месяцы | Айлар |
| `editAssign.wizard.sumExecutors` | Executors | Исполнители | Орындаушылар |
| `editAssign.wizard.sumEffect` | They will be able to edit these cells even without write rights in their role (within your own rights). | Они смогут править эти ячейки, даже если по роли у них нет права записи (в пределах ваших прав). | Олар рөлі бойынша жазу құқығы болмаса да осы ұяшықтарды өңдей алады (сіздің құқықтарыңыз шегінде). |
| `editAssign.wizard.sumExclusive` | Others can’t edit {months} until it is validated or removed. | Остальные не смогут править {months}, пока он не валидирован или не снят. | Басқалар {months} валидацияланғанша немесе алынғанша өңдей алмайды. |
| `editAssign.wizard.sumOpen` | Others with write rights in their role can still edit {months}. | Остальные с правом записи по роли по-прежнему смогут править {months}. | Рөлі бойынша жазу құқығы бар басқалар {months} өңдей алады. |
| `editAssign.wizard.noVisible` | Executors who cannot open this document yet: {n}. The handover is saved anyway; ask an administrator to give them the role “Executor”. | Исполнителей, которые пока не видят документ: {n}. Передача всё равно сохранится; попросите администратора выдать им роль «Исполнитель». | Құжатты әлі көре алмайтын орындаушылар: {n}. Беру бәрібір сақталады; әкімшіден оларға «Орындаушы» рөлін беруін сұраңыз. |
| `editAssign.bar.assigned` | {month} was handed to you | Месяц «{month}» передан вам | «{month}» айы сізге берілді |
| `editAssign.bar.assignedText` | Fill in the cells, then mark the month as filled. | Заполните ячейки и отметьте месяц как заполненный. | Ұяшықтарды толтырып, айды толтырылған деп белгілеңіз. |
| `editAssign.bar.returned` | {month} was returned by the owner | Месяц «{month}» возвращён владельцем | «{month}» айын иесі қайтарды |
| `editAssign.bar.filled` | {month}: filled — awaiting validation | «{month}»: заполнено — ждёт валидации | «{month}»: толтырылды — валидацияны күтуде |
| `editAssign.bar.filledText` | You can’t edit this month until the owner returns it. | Вы не можете править этот месяц, пока владелец его не вернёт. | Иесі қайтармайынша бұл айды өңдей алмайсыз. |
| `editAssign.bar.validated` | {month}: validated by the owner | «{month}»: валидировано владельцем | «{month}»: иесі валидациялады |
| `editAssign.bar.validatedText` | The month is closed for editing. | Месяц закрыт для правки. | Ай өңдеуге жабық. |
| `editAssign.bar.markFilled` | Mark as filled | Отметить как заполненное | Толтырылды деп белгілеу |
| `editAssign.bar.confirmFill` | After this you can’t edit {month} until the owner returns it. | После этого вы не сможете править «{month}», пока владелец его не вернёт. | Осыдан кейін иесі қайтармайынша «{month}» айын өңдей алмайсыз. |
| `editAssign.bar.ownerFilled` | {month} is filled — awaiting your validation | Месяц «{month}» заполнен — ждёт вашей валидации | «{month}» айы толтырылды — сіздің валидацияңызды күтуде |
| `editAssign.bar.ownerFilledText` | All executors marked it as filled. Check the figures, then validate it or return it with a comment. | Все исполнители отметили его как заполненный. Проверьте цифры, затем валидируйте или верните с комментарием. | Барлық орындаушылар оны толтырылды деп белгіледі. Сандарды тексеріп, валидациялаңыз немесе пікірмен қайтарыңыз. |
| `editAssign.toast.filled` | {month} marked as filled — the owner is notified | Месяц «{month}» отмечен как заполненный — владелец уведомлён | «{month}» айы толтырылды деп белгіленді — иесіне хабарланды |
| `editAssign.validate.title` | Validate {month}? | Валидировать «{month}»? | «{month}» айын валидациялау керек пе? |
| `editAssign.validate.c1` | All executors marked {month} as filled. | Все исполнители отметили «{month}» как заполненный. | Барлық орындаушылар «{month}» айын толтырылды деп белгіледі. |
| `editAssign.validate.c2` | Executors can’t edit {month} after validation. | После валидации исполнители не смогут править «{month}». | Валидациядан кейін орындаушылар «{month}» айын өңдей алмайды. |
| `editAssign.validate.c3` | You can return it with a comment later. | Позже вы сможете вернуть его с комментарием. | Кейінірек оны пікірмен қайтара аласыз. |
| `editAssign.validate.verb` | Validate | Валидировать | Валидациялау |
| `editAssign.validate.blocked` | Not everyone has finished | Заполнили не все | Бәрі бітірген жоқ |
| `editAssign.validate.blockedText` | {names} did not mark {month} as filled yet. | {names} ещё не отметили «{month}» как заполненный. | {names} «{month}» айын әлі толтырылды деп белгілеген жоқ. |
| `editAssign.return.title` | Return {month} with a comment | Вернуть «{month}» с комментарием | «{month}» айын пікірмен қайтару |
| `editAssign.return.label` | Comment for the executors | Комментарий для исполнителей | Орындаушыларға пікір |
| `editAssign.return.hint` | 1–500 characters. Only the returned executors and managers see it. | От 1 до 500 символов. Видят только возвращаемые исполнители и менеджеры. | 1–500 таңба. Тек қайтарылатын орындаушылар мен менеджерлер көреді. |
| `editAssign.return.to` | Return to | Вернуть | Кімге қайтару |
| `editAssign.return.verb` | Return | Вернуть | Қайтару |
| `editAssign.remove.title` | Remove the handover of {month}? | Снять передачу месяца «{month}»? | «{month}» айын беруді алып тастау керек пе? |
| `editAssign.remove.c1` | Executors lose the right to edit {month}. | Исполнители теряют право править «{month}». | Орындаушылар «{month}» айын өңдеу құқығынан айырылады. |
| `editAssign.remove.c2` | Values already entered stay. | Уже введённые значения сохраняются. | Енгізілген мәндер сақталады. |
| `editAssign.remove.c3` | The audit trail keeps the record of who had access. | Журнал аудита сохранит запись о том, кто имел доступ. | Аудит журналы кімде қолжетімділік болғанын сақтайды. |
| `editAssign.remove.verb` | Remove handover | Снять передачу | Беруді алып тастау |
| `editAssign.result.assigned` | Handed over: {months} | Передано: {months} | Берілді: {months} |
| `editAssign.result.assignedText` | Executors see it in My tasks. You get a note when all of them mark it as filled. | Исполнители увидят это в «Моих задачах». Когда все отметят месяц как заполненный, вы получите уведомление. | Орындаушылар мұны «Менің тапсырмаларымда» көреді. Бәрі толтырылды деп белгілегенде сізге хабарланады. |
| `editAssign.result.validated` | Validated: {month} | Валидировано: {month} | Валидацияланды: {month} |
| `editAssign.result.validatedText` | Executors are notified. {month} stays locked for them. | Исполнители уведомлены. Для них «{month}» остаётся заблокированным. | Орындаушыларға хабарланды. Олар үшін «{month}» бұғатталған күйінде қалады. |
| `editAssign.result.returned` | Returned: {month} | Возвращено: {month} | Қайтарылды: {month} |
| `editAssign.result.returnedText` | The returned executors see your comment above the table and in My tasks. | Возвращённые исполнители увидят ваш комментарий над таблицей и в «Моих задачах». | Қайтарылған орындаушылар пікіріңізді кестенің үстінде және «Менің тапсырмаларымда» көреді. |
| `editAssign.toast.removed` | Handover of {month} removed | Передача месяца «{month}» снята | «{month}» айын беру алынды |
| `editAssign.err.noMonths` | Choose at least one month. | Выберите хотя бы один месяц. | Кемінде бір айды таңдаңыз. |
| `editAssign.err.noExecutors` | Choose at least one executor. | Выберите хотя бы одного исполнителя. | Кемінде бір орындаушыны таңдаңыз. |
| `editAssign.err.setName` | Give the set a name or untick “Save as set”. | Назовите набор или снимите «Сохранить как набор». | Жинаққа ат беріңіз немесе «Жинақ ретінде сақтау» белгісін алыңыз. |
| `editAssign.err.exceeds` | You can’t hand over cells you can’t edit yourself: sheets {sheets}. Narrow the handover to one sheet you can edit. | Нельзя передавать ячейки, которые вы сами не можете править: листы {sheets}. Сузьте передачу до листа, который вам доступен. | Өзіңіз өңдей алмайтын ұяшықтарды беруге болмайды: {sheets} парақтары. Берілімді өзіңізге қолжетімді бір парақпен шектеңіз. |
| `editAssign.err.nextClosed` | The next period is closed — it can’t be handed over. | Следующий период закрыт — передать его нельзя. | Келесі кезең жабық — оны беруге болмайды. |
| `editAssign.err.noNext` | There is no next month in this project year. | В этом проектном году нет следующего месяца. | Бұл жоба жылында келесі ай жоқ. |
| `myTasks.assignToFill` | Assigned to me | Назначено мне | Маған тағайындалған |
| `myTasks.awaitingValidation` | Awaiting my validation | Ждёт моей валидации | Менің валидациямды күтуде |
| `myTasks.open` | Open | Открыть | Ашу |
| `myTasks.returnComment` | Owner’s comment | Комментарий владельца | Иесінің пікірі |
| `editorSet.title` | Executor sets | Наборы исполнителей | Орындаушылар жинақтары |
| `editorSet.hint` | A set is a saved list of people. Applying it copies the names, so later edits of a set do not change handovers already made. | Набор — сохранённый список людей. При применении имена копируются, поэтому правка набора не меняет уже сделанные передачи. | Жинақ — адамдардың сақталған тізімі. Қолданғанда аттар көшіріледі, сондықтан жинақты өзгерту бұрын жасалған берулерді өзгертпейді. |
| `editorSet.members` | Members | Участники | Мүшелер |
| `editorSet.new` | New set | Новый набор | Жаңа жинақ |
| `editorSet.save` | Save set | Сохранить набор | Жинақты сақтау |
| `editorSet.delete` | Delete | Удалить | Жою |
| `editorSet.deleteAsk` | Delete set “{name}”? | Удалить набор «{name}»? | «{name}» жинағын жою керек пе? |
| `editorSet.empty` | No sets yet. Tick “Save as set” in the wizard or create one here. | Наборов пока нет. Отметьте «Сохранить как набор» в мастере или создайте здесь. | Жинақтар әлі жоқ. Шебердегі «Жинақ ретінде сақтау» белгісін қойыңыз немесе осында жасаңыз. |
| `editorSet.err.members` | Pick at least one member. | Выберите хотя бы одного участника. | Кемінде бір мүшені таңдаңыз. |
