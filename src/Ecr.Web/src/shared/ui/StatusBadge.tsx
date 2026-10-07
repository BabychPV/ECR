import type { CSSProperties, JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Статусний бейдж набору (`KIT.md` §6.7, крок `UI-04` директиви №15).
 *
 * ⛔ **Це єдине місце, де стан перетворюється на видиме.** Директива №15 §2
 * «Шар 1» формулює це прямо: «таблиця `kind × state → tone` — один об'єкт,
 * тест перебирає всі пари й вимагає рядок `status.<kind>.<state>` у каталозі.
 * **Іншого способу намалювати статус у застосунку не лишається**». Тому
 * підпис береться з каталогу за ключем, а не приходить пропом: проп дозволив
 * би кожному екранові передати що завгодно, і компонент був би не джерелом, а
 * рамкою навколо чужого тексту.
 *
 * ✎ 2026-09-19. Абзац вище закінчувався переліком п'яти живих способів, які
 * вже розійшлися: `JobsPage.stateColor` і `PeriodsPage.stateColor` малювали
 * невідомий стан синім (причому в `PeriodsPage` туди ж потрапляв `Scheduled`);
 * `SourcesPage` фарбував `Failed` у `statusWarning`; `HealthPage` мав власну
 * трійку; `DocumentsPage` не фарбував узагалі й друкував сирий код сервера.
 * **Усіх п'ятьох більше немає** — #399, #402 і цей PR перевели кожен екран
 * сюди, а самі помічники видалено. Перелік лишається записом того, ЧОМУ це
 * місце єдине, а не описом чинного стану: ціною розходження були чотири різні
 * відповіді на те саме питання «що означає стан, якого ми не знаємо».
 *
 * ⛔ **Стани взяті з КОДУ СЕРВЕРА, не з `KIT.md`.** Макет писався під
 * демо-дані й називає те, чого в домені немає: `sheet/Returned` (повернення
 * пише `Draft` — `ApprovalState.Reopen()`), `period/Archived` і
 * `period/NotOpened` (`PeriodState` має `Scheduled`), `job/Done` (сервер каже
 * `Succeeded`), `version/Archived` (`TemplateVersionStatus.Deprecated`),
 * `severity/Critical` (`ValidationSeverity` = `Info|Warning|Error`) і ВЕСЬ
 * різновид `user` (на сервері це чотири незалежні булеві поля `UserView`, а
 * `Invited` не відповідає нічому). Перелік розбіжностей — у звіті PR.
 */

/**
 * Тони набору.
 *
 * ⛔ Тону «успіх» (зеленого) тут НЕМАЄ, і це не пропуск. `KIT.md` §1.3 і §1
 * «Тони» задають правило прямо: «**Зелений не вживається для „все гаразд“** —
 * лише в `ResultBanner` успіху одразу після дії». Нормальний стан
 * нейтральний; кольором позначається лише те, що не так або чекає дії.
 *
 * ⛔ Тону `critical` (суцільний червоний із `KIT.md` §6.7) теж немає, і з тієї
 * самої причини: єдиний стан, що його вимагав, — `severity/Critical` — на
 * сервері не існує. Колір, до якого не веде жоден стан, — це та сама мертва
 * гілка, що й `Grace`/`Closed` у `ProjectStatus` (`D-123`): вона з'явилась би
 * у фільтрах і не дала б жодного рядка.
 *
 * `info` — це і «чекає дії» (`Submitted`), і «в роботі» (`Running`): за
 * `KIT.md` це один тон, бо обидва означають «система чи людина зараз цим
 * зайняті», і розводити їх кольором означало б додати різницю, якої макет не
 * малює.
 */
export const statusTones = ['neutral', 'info', 'warning', 'danger', 'muted'] as const;

/** Тон статусу. */
export type StatusTone = (typeof statusTones)[number];

/** Пара токенів «текст на власному тлі». */
export interface ToneFill {
  readonly text: string;
  readonly bg: string;
}

/**
 * Тон → токени кольору.
 *
 * ⛔ Пара названа ЯВНО, а не віддана резолверу варіантів Mantine, і причина
 * виміряна: `variant="light" color="gray"` дає у світлій темі `gray[6]`
 * (`#868e96`) на 10 %-заливці того ж кольору — ≈2.9:1, тобто провал AA. Це
 * той самий дефект, що `W4.2` (`red`/`orange`) і `Q-262` (`green`), просто на
 * сірому: стандартні шкали Mantine підібрані під типовий `primaryShade` (8), а
 * ця тема задає `{ light: 6, dark: 5 }`. Токени `--ecr-*` під цю тему вже
 * виміряні (`cssVariables.ts`), тож тон бере їх, а не шкалу.
 *
 * ⛔ Значення — лише посилання `var(--ecr-*)`. Літерала кольору тут бути не
 * може (`ФВ-14.11`), і `shared/ui/**` не є винятком у конфігу лінтера —
 * виняток лише `shared/theme/**`.
 */
export const toneFills: Readonly<Record<StatusTone, ToneFill>> = {
  neutral: { text: 'var(--ecr-text)', bg: 'var(--ecr-sunken)' },
  info: { text: 'var(--ecr-accent-text)', bg: 'var(--ecr-accent-soft)' },
  warning: { text: 'var(--ecr-warning)', bg: 'var(--ecr-sunken)' },
  danger: { text: 'var(--ecr-danger)', bg: 'var(--ecr-sunken)' },
  muted: { text: 'var(--ecr-muted)', bg: 'var(--ecr-sunken)' },
};

/** Різновид статусу — одна словникова одиниця сервера. */
export type StatusKind =
  | 'sheet'
  | 'period'
  | 'job'
  | 'version'
  | 'project'
  | 'health'
  | 'severity'
  | 'collectionRun'
  | 'coverage'
  | 'snapshot'
  | 'notificationDelivery';

/**
 * Таблиця `kind × state → tone` — **один об'єкт** (директива №15 §2).
 *
 * ⚠ Ключі станів — рядки, а не літеральний union, навмисно: половина цих
 * словників доходить до клієнта НЕТИПІЗОВАНОЮ. `sheetStates` оголошено як
 * `{ [key: string]: string }` (`schema.d.ts:9133`), `JobStatus.state` і
 * `JobSummary.state` — просто `string` (`:9452`, `:9476`), `HealthReportDto
 * .status` — теж (`:9401`). Тобто невідомий стан не гіпотеза: він приїде
 * першим же поповненням серверного переліку, і компілятор про це не скаже.
 * Поведінка на ньому описана в `UnknownStateTone` нижче.
 */
export const statusTable: Readonly<Record<StatusKind, Readonly<Record<string, StatusTone>>>> = {
  /**
   * `Ecr.Domain/Enums/Enums.cs` → `DocumentStatus`; значення словника
   * `DocumentSummary.sheetStates` (`DocumentStore.StatesAsync`:
   * `s => s.Status.ToString()`). Дзеркало переходів —
   * `features/workflow/transitions.ts`.
   *
   * ⚠ Скалярного статусу ДОКУМЕНТА не існує (`D-93`): стан живе на парі
   * «аркуш × період», тому різновид зветься `sheet`, а не `document`.
   */
  sheet: {
    Draft: 'neutral',
    Submitted: 'info',
    Approved: 'neutral',
    Rejected: 'danger',
  },

  /** `Ecr.Domain/Enums/Enums.cs` → `PeriodState` (`schema.d.ts:10280`). */
  period: {
    // Ще не відкрито: бляклий — стан є, але робити в ньому нічого.
    Scheduled: 'muted',
    Open: 'neutral',
    // Пільговий строк: ще можна, але вже недовго — саме «увага» (`KIT.md` §1).
    Grace: 'warning',
    Closed: 'neutral',
  },

  /**
   * `Ecr.Application/Integration/IntegrationHandlers.cs` → `KnownStates`
   * (`["Queued","Running","Succeeded","Failed","Cancelled"]`) плюс два, які
   * віддає лише `GET /jobs/{jobId}` (`QuartzJobScheduler.cs:365`, `:369`).
   *
   * ⚠ `Unknown` і `Unavailable` — НЕ помилка задачі, а відмова відповісти про
   * неї: планувальник вимкнено або ідентифікатора вже немає. Тому `warning`
   * («розберіться»), а не `danger` («задача впала») — інакше оператор шукав
   * би причину збою там, де збою не було.
   */
  job: {
    Queued: 'neutral',
    Running: 'info',
    Succeeded: 'neutral',
    Failed: 'danger',
    Cancelled: 'muted',
    Unknown: 'warning',
    Unavailable: 'warning',
    // ⛔ Похідні стани розкладу (P4 ФВ-9.8, `JobStatus.effectiveState`): батько вже
    // `Succeeded`, а документи ще рахуються / частина впала — це не «успішно».
    FannedOut: 'info',
    SucceededWithErrors: 'warning',
  },

  /**
   * `Ecr.Domain/Enums/Enums.cs` → `TemplateVersionStatus`
   * (`schema.d.ts:11849`). ⚠ Той самий перелік використовує і
   * `MethodologyVersion.Status` — словник один, не два.
   */
  version: {
    Draft: 'neutral',
    Published: 'neutral',
    Deprecated: 'muted',
  },

  /** `Ecr.Domain/Enums/Enums.cs` → `ProjectStatus` (`schema.d.ts:10349`). */
  project: {
    Draft: 'neutral',
    Active: 'neutral',
    Archived: 'muted',
  },

  /**
   * `Ecr.Api/Health/HealthReportDto.cs:19` — стрінгіфікований
   * `HealthStatus` платформи.
   *
   * ⚠ `Degraded` — саме `warning`, не `danger`: «система працює, але чогось у
   * ній бракує». Це вже записане рішення (`HealthPage.badgeColor`), і
   * показувати його червоним означало б навчити оператора не дивитися на
   * червоне.
   */
  health: {
    Healthy: 'neutral',
    Degraded: 'warning',
    Unhealthy: 'danger',
  },

  /**
   * `Ecr.Domain/Enums/Enums.cs` → `ValidationSeverity`
   * (`schema.d.ts:12200`). ⚠ `Critical` із `KIT.md` тут немає — його немає й
   * на сервері.
   */
  severity: {
    Info: 'neutral',
    Warning: 'warning',
    Error: 'danger',
  },

  /**
   * Результат збору з зовнішнього джерела (`Adapters.PiAf/CollectionRunner.cs`,
   * `schema.d.ts:8602`).
   *
   * ⛔ Окремий різновид, а не `health`, хоч слова збігаються: там `Healthy`,
   * тут `Succeeded`, і спільного між словниками — саме `Degraded`. Звести їх
   * в один означало б, що майбутня зміна одного мовчки перефарбує інший.
   *
   * ⚠ Заведено тому, що чинний виклик помилковий: `SourcesPage.tsx:118`
   * малює `Failed` як `statusWarning` — тобто провал збору виглядає як
   * попередження.
   */
  collectionRun: {
    Succeeded: 'neutral',
    Degraded: 'warning',
    Failed: 'danger',
  },

  /**
   * Подія журналу покриття (`CollectionCoverage.KnownStatuses`, `D-118`):
   * інтервал зібрано, але в комірки він не ліг.
   *
   * ⚠ `SkippedPointCeiling` — `danger`: значення за поле не лягло зовсім, і
   * виправити це може лише людина. `SkippedPeriodClosed` — `warning`: період
   * закрито навмисно, але пізні дані все одно треба звірити. `ConflictKeptManual`
   * — `info`: ручне значення збережено за правилом, це не збій.
   * `SkippedWriteConflict` — `warning`: значення не записано, хоч наступний
   * прогін і спробує знову. `SkippedNeedsConfirmation` — `warning`, а не
   * `info`: значення не записано, і без дії людини (підтвердження) воно не
   * ляже; `info` тут означав би «нічого робити не треба», як у `ConflictKeptManual`.
   *
   * Події синку довідника (`RegistrySyncJob`, FEATURE-REGISTRY-SYNC S5):
   * `RegistryValueRejected` — `danger` (значення джерела не лягло б у поле ніколи
   * без правки мапінгу чи джерела); `RegistryDiverged`, `RegistrySourceMissing`,
   * `RegistryElementUnlinked` — `warning` (довідник і джерело розійшлися, потрібне
   * рішення людини: звірити, прив'язати); `RegistryConflictKeptManual` і
   * `RegistryPendingUpdate` — `info`: перше — правило `D-118`, друге — лише звірка
   * S5, синк ще не пише.
   *
   * `SourceDataRefused` (збір, `CollectionRunner`) — `danger`: джерело відповідає,
   * але дані інтервалу віддати не може (напр. нечитабельна мітка часу), і
   * наздоганяння без правки джерела чи запиту не допоможе.
   *
   * Синк за політикою `D-212`: `RegistryAutoCreated` — `info` (синк `External`
   * зробив свою роботу); `RegistryDeactivated`, `RegistryReactivated`,
   * `RegistryRuleViolation`, `RegistryExternalKeyRelinked` — `warning`: довідник
   * змінився без людини або чекає її рішення. Та сама вага, що в
   * `NotificationJob.SeverityOf`.
   */
  coverage: {
    SkippedPointCeiling: 'danger',
    SkippedPeriodClosed: 'warning',
    SkippedWriteConflict: 'warning',
    SkippedNeedsConfirmation: 'warning',
    ConflictKeptManual: 'info',
    RegistryDiverged: 'warning',
    RegistryConflictKeptManual: 'info',
    RegistrySourceMissing: 'warning',
    RegistryElementUnlinked: 'warning',
    RegistryValueRejected: 'danger',
    RegistryPendingUpdate: 'info',
    SourceDataRefused: 'danger',
    RegistryAutoCreated: 'info',
    RegistryDeactivated: 'warning',
    RegistryReactivated: 'warning',
    RegistryRuleViolation: 'warning',
    RegistryExternalKeyRelinked: 'warning',
    // ФВ-13.15: плановий збір пропущено, бо розклад-залежність ще не відбіг. `info` (той самий
    // тон і токени, що `ConflictKeptManual`): це затримка за правилом, а не збій.
    SkippedDependency: 'info',
  },

  /**
   * `Ecr.Domain/Enums/Enums.cs` → `SnapshotStatus` (`D-65`); до клієнта їде
   * рядком (`ReportSnapshotSummary.status: string`).
   *
   * ⛔ Окремий різновид, а не `sheet`, хоч три слова збігаються: у зрізі немає
   * `Rejected`, а `Submitted` тут — не «чекає погодження», а «подано,
   * іммутабельний» (`ФВ-9.17`), тобто кінцевий стан, а не `info`.
   * `Draft` бляклий: зріз є, але регуляторна вʼюха його не віддає (`ФВ-10.11`).
   */
  snapshot: {
    Draft: 'muted',
    Approved: 'neutral',
    Submitted: 'neutral',
  },

  /**
   * `Ecr.Domain/Enums/Enums.cs` → `NotificationDeliveryStatus` (`BE-33`,
   * `schema.d.ts`: `"Sent" | "Failed" | "Suppressed"`).
   *
   * ⛔ Окремий різновид, а не `job`, хоч слово `Failed` збігається: там падіння
   * ЗАДАЧІ, тут — недоставлене сповіщення про неї. Звести їх означало б, що
   * зміна одного словника мовчки перефарбує інший.
   *
   * ⚠ `Suppressed` — `muted`, не `warning`: подію навмисно не надіслали
   * (дедуплікація за `eventKey`), це нормальна робота, а не привід
   * розбиратися. Жовтий тут навчив би не дивитися на жовте.
   */
  notificationDelivery: {
    Sent: 'neutral',
    Failed: 'danger',
    Suppressed: 'muted',
  },
};

/**
 * Тон невідомого стану.
 *
 * ⛔ Рішення назване, а не виведене «як вийде». Розглянуто три:
 *
 *   • **кинути виняток** — сервер розширює переліки, і половина з них доходить
 *     нетипізованою (див. коментар до `statusTable`); один новий рядок клав би
 *     весь перелік документів;
 *   • **нейтральний** — ТИХО, і саме тому відкинуто. Нейтральний тон у цьому
 *     наборі не «без кольору», а твердження «нормальний стан» (`KIT.md`
 *     §1.3): новий `Corrupted` носив би його роками, доки хтось випадково не
 *     відкриє DevTools;
 *   • **відмова (`danger`)** — брехня в інший бік: невідомий стан не є
 *     проблемою ДОКУМЕНТА, і червоний бейдж послав би користувача шукати
 *     неіснуючу помилку в даних.
 *
 * ⚠ `warning` — рівно те твердження, яке тут правдиве: «увага, розберіться»
 * (`KIT.md` §1, «Тони»). Розбиратися треба не з документом, а з тим, що клієнт
 * відстав від сервера.
 *
 * ⚠ Помітність несе не лише колір, і це навмисно — колір один із трьох
 * каналів, а не єдиний (`ФВ-14.18`):
 *   1. тон `warning`, який неможливо прочитати як «усе гаразд»;
 *   2. підпис: рядка `status.<kind>.<state>` у каталозі теж немає, тож `t()`
 *      повертає позначений ключ `⟦status.job.Superseded⟧` — `D-138` заводив
 *      ці дужки саме для того, щоб пропуск було видно «з першого погляду і з
 *      будь-якої відстані»;
 *   3. `console.error('Немає рядка інтерфейсу: …')` у режимі розробки — той
 *      самий механізм `t()`, без жодного власного коду тут.
 * Плюс `data-status-known="false"` у розмітці — для тестів і прогонів e2e.
 */
export const UnknownStateTone: StatusTone = 'warning';

/** Чи є ця пара `kind`/`state` у таблиці набору. */
export function isKnownStatus(kind: StatusKind, state: string): boolean {
  return statusTable[kind][state] !== undefined;
}

/** Тон пари `kind`/`state`; невідомий стан — `UnknownStateTone`. */
export function statusTone(kind: StatusKind, state: string): StatusTone {
  return statusTable[kind][state] ?? UnknownStateTone;
}

/**
 * Ключ каталогу для підпису статусу.
 *
 * ⚠ Стан підставляється в ключ ТАК, ЯК ЙОГО НАЗВАВ СЕРВЕР (`Draft`, а не
 * `draft`): це вже усталена в цьому каталозі форма для ключів, похідних від
 * переліку домену — пор. `deny.OutsidePermitWindow`, `deny.ColumnReadOnly`
 * (`09-seed.sql`). Приведення регістру між значенням сервера й ключем було б
 * зайвим перетворенням, яке нема кому перевірити, і першим же джерелом
 * розбіжності `ReadOnlyColumn` проти `ColumnReadOnly` (`A7-02`).
 */
export function statusKey(kind: StatusKind, state: string): string {
  return `status.${kind}.${state}`;
}

/**
 * Вигляд бейджа за тоном (`UI-27`, `KIT.md` §6.7, `index.html` `.badge*`).
 *
 * ⛔ Окремо від `toneFills`, і це не дубль. `toneFills` — колір ТЕКСТУ стану
 * поза бейджем (лічильники етапів, `DocumentListSummaryStrip`), а тут — уся
 * плашка: тло, рамка, підпис і піктограма. Макет розводить їх прямо: у
 * бейджі нейтральний стан — без заливки, з тонкою рамкою, а підпис проблеми —
 * звичайним кольором тексту на м'якому тлі; колір несе тло й піктограма
 * (`.badge.bad{background:var(--danger-soft)} .badge.bad svg{color:var(--danger)}`).
 *
 * ⚠ Відхилення від макета одне, і воно на користь контрасту: у `quiet` макет
 * фарбує підпис у `--muted` для БУДЬ-ЯКОГО тону, тобто й поверх м'якого тла
 * проблеми. Тут приглушується лише нейтральний і бляклий — підпис на
 * кольоровому тлі лишається основним кольором тексту.
 */
interface BadgeLook {
  readonly text: string;
  readonly bg: string;
  readonly border: string;
  readonly icon: string;
}

const badgeLooks: Readonly<Record<StatusTone, BadgeLook>> = {
  neutral: { text: 'var(--ecr-text)', bg: 'transparent', border: 'var(--ecr-border)', icon: 'var(--ecr-muted)' },
  muted: { text: 'var(--ecr-muted)', bg: 'transparent', border: 'var(--ecr-border)', icon: 'var(--ecr-muted)' },
  info: { text: 'var(--ecr-text)', bg: 'var(--ecr-accent-soft)', border: 'transparent', icon: 'var(--ecr-accent-text)' },
  warning: { text: 'var(--ecr-text)', bg: 'var(--ecr-warning-soft)', border: 'transparent', icon: 'var(--ecr-warning)' },
  danger: { text: 'var(--ecr-text)', bg: 'var(--ecr-danger-soft)', border: 'transparent', icon: 'var(--ecr-danger)' },
};

/** Вигляд бейджа тону — для тестів і для `SegmentBar`, що малює ті самі стани. */
export function badgeLook(tone: StatusTone): BadgeLook {
  return badgeLooks[tone];
}

/**
 * Піктограми станів (`kit.js` → `BADGES`, другий елемент кожного запису),
 * шляхи — з `kit.js` → `I` дослівно (viewBox 24, лінія).
 *
 * ⚠ Ключ — СЛОВО стану, а не пара `kind/state`: макет дає тому самому слову
 * ту саму піктограму в усіх різновидах (`Draft` — олівець і в `sheet`, і в
 * `version`). Слова, якого тут немає, отримують піктограму ТОНУ
 * (`toneIcons`), тож новий стан сервера не лишається без другого знака.
 */
const C9 = 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z';
const iconPaths = {
  pencil: 'M4 20l4-1L19 8l-3-3L5 16z',
  send: 'M21 3L10 14M21 3l-7 18-4-7-7-4z',
  checkCircle: C9 + 'M8 12.5l3 3 5-6',
  xCircle: C9 + 'M9 9l6 6M15 9l-6 6',
  alertCircle: C9 + 'M12 7.5v5.5M12 16v.5',
  alert: 'M12 4l9 16H3zM12 10v4M12 17v.5',
  clock: C9 + 'M12 7v5l3 2',
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  archive: 'M3 5h18v4H3zM5 9v10h14V9M10 13h4',
  minus: 'M5 12h14',
  circle: 'M12 19a7 7 0 1 0 0-14 7 7 0 0 0 0 14z',
  info: C9 + 'M12 11v6M12 7.5v.5',
  spin: 'M12 3a9 9 0 1 0 9 9',
} as const;

type IconName = keyof typeof iconPaths;

const stateIcons: Readonly<Record<string, IconName>> = {
  Draft: 'pencil',
  Submitted: 'send',
  Approved: 'checkCircle',
  Rejected: 'xCircle',
  Scheduled: 'clock',
  Open: 'circle',
  Grace: 'clock',
  Closed: 'lock',
  Queued: 'clock',
  Running: 'spin',
  FannedOut: 'spin',
  Succeeded: 'checkCircle',
  SucceededWithErrors: 'alert',
  Failed: 'xCircle',
  Cancelled: 'minus',
  Published: 'checkCircle',
  Deprecated: 'archive',
  Active: 'checkCircle',
  Archived: 'archive',
  Healthy: 'checkCircle',
  Degraded: 'alert',
  Unhealthy: 'xCircle',
  Info: 'info',
  Warning: 'alert',
  Error: 'alertCircle',
  Sent: 'checkCircle',
  Suppressed: 'minus',
};

const toneIcons: Readonly<Record<StatusTone, IconName>> = {
  neutral: 'circle',
  muted: 'minus',
  info: 'info',
  warning: 'alert',
  danger: 'xCircle',
};

/** Ім'я піктограми стану: власна піктограма слова, інакше — піктограма тону. */
export function statusIconName(kind: StatusKind, state: string): IconName {
  // ⛔ Невідомий стан — завжди «увага», навіть якщо слово збіглося з відомим
  // в іншому різновиді: піктограма не має казати більше, ніж тон.
  if (!isKnownStatus(kind, state)) return toneIcons[UnknownStateTone];

  return stateIcons[state] ?? toneIcons[statusTone(kind, state)];
}

function StatusIcon({ name }: { readonly name: IconName }): JSX.Element {
  return (
    <svg
      width={12}
      height={12}
      viewBox="0 0 24 24"
      fill="none"
      stroke="var(--ecr-badge-icon)"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      data-status-icon={name}
      style={{ flex: 'none', display: 'block' }}
    >
      <path d={iconPaths[name]} />
    </svg>
  );
}

export interface StatusBadgeProps {
  /** Різновид статусу. */
  readonly kind: StatusKind;

  /** Код стану, як його назвав сервер. */
  readonly state: string;

  /** Без рамки — для щільних таблиць (`KIT.md` §6.7 `quiet`). */
  readonly quiet?: boolean | undefined;

  /** Підказка при наведенні. */
  readonly title?: string | undefined;
}

/**
 * Статус як бейдж.
 *
 * ⚠ Другий носій змісту — сам ПІДПИС (`ФВ-14.18`): стан названий словом, а не
 * самим лише кольором, тож дихромат і монохромний друк читають його так само.
 * Третій — піктограма стану (`UI-27`): олівець, галочка, літак, хрестик.
 *
 * ✎ `UI-27` (2026-10-06): вигляд — за макетом (`index.html` `.badge`): звичайний
 * регістр замість жирної капітелі, вага 500, висота 20, радіус `--r1` (4),
 * піктограма 12 px ліворуч. Нейтральний стан — без заливки, з тонкою рамкою;
 * «чекає дії» і проблеми — м'яке тло без рамки (`badgeLooks`). Таблиця станів
 * (`statusTable`) і тони — БЕЗ змін.
 */
export function StatusBadge({ kind, state, quiet = false, title }: StatusBadgeProps): JSX.Element {
  const tone = statusTone(kind, state);
  const look = badgeLooks[tone];
  const plain = tone === 'neutral' || tone === 'muted';

  /*
   * ⚠ `quiet` (макет `.badge.quiet`): без рамки і без лівого відступу —
   * бейдж стає «піктограма + слово» у щільній таблиці. Нейтральний підпис ще
   * й приглушується (вага 400, `--ecr-muted`); тло проблеми лишається — колір
   * у таблиці мусить бути там, де щось не так (`KIT.md` §1.3).
   */
  const text = quiet && plain ? 'var(--ecr-muted)' : look.text;
  const border = quiet ? 'transparent' : look.border;

  /*
   * ⛔ `miw="fit-content"` + `width: max-content` — інваріант «підпис не
   * стискається нижче власного тексту» (UI-аудит, lane 8, і 2026-09-25,
   * `period/Grace`): `table-layout: auto` інакше обрізав «Grace period» до
   * «Grace…». Подробиці вимірів — в історії цього файла (git log -L). Через
   * `style`, а не `w=`, бо `ФВ-14.30` забороняє проп `w` на `Badge`.
   *
   * ⚠ Mantine `Badge` лишається коренем (тести й сторінки шукають
   * `.mantine-Badge-root`), але все, що робить його «капітельним»,
   * перебито: `textTransform`, `fontWeight`, `letterSpacing`.
   */
  return (
    <Badge
      size="sm"
      variant="transparent"
      miw="fit-content"
      radius="xs"
      c={text}
      bg={look.bg}
      leftSection={<StatusIcon name={statusIconName(kind, state)} />}
      styles={{
        root: {
          height: 20,
          paddingInline: quiet ? '0 var(--mantine-spacing-xs)' : '4px var(--mantine-spacing-xs)',
          border: `1px solid ${border}`,
          textTransform: 'none',
          letterSpacing: 'normal',
          fontWeight: quiet && plain ? 400 : 500,
          fontSize: 'var(--mantine-font-size-xs)',
          gap: 4,
        },
        section: { marginInlineEnd: 0 },
      }}
      style={
        {
          width: 'max-content',
          '--ecr-badge-icon': look.icon,
        } as CSSProperties
      }
      title={title}
      data-status-kind={kind}
      data-status-state={state}
      data-status-tone={tone}
      data-status-known={String(isKnownStatus(kind, state))}
      data-status-quiet={quiet ? 'true' : undefined}
    >
      {t(statusKey(kind, state))}
    </Badge>
  );
}
