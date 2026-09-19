import type { JSX } from 'react';
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
  | 'collectionRun';

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

export interface StatusBadgeProps {
  /** Різновид статусу. */
  readonly kind: StatusKind;

  /** Код стану, як його назвав сервер. */
  readonly state: string;

  /** Без заливки — для щільних таблиць (`KIT.md` §6.7 `quiet`). */
  readonly quiet?: boolean | undefined;

  /** Підказка при наведенні. */
  readonly title?: string | undefined;
}

/**
 * Статус як бейдж.
 *
 * ⚠ Другий носій змісту — сам ПІДПИС (`ФВ-14.18`): стан названий словом, а не
 * самим лише кольором, тож дихромат і монохромний друк читають його так само.
 */
export function StatusBadge({ kind, state, quiet = false, title }: StatusBadgeProps): JSX.Element {
  const tone = statusTone(kind, state);
  const fill = toneFills[tone];

  /*
   * ⚠ `default`, а не `light`: варіант `light` без явного `color` бере
   * ФІРМОВИЙ відтінок (`--mantine-primary-color-light`) — тобто нейтральний
   * стан приїхав би з синюватою заливкою під нашою. `default` бере
   * `--mantine-color-default*`, які `cssVariables.ts` уже виводить із
   * поверхонь теми, і дає рамку — саме її `KIT.md` §6.7 знімає прапорцем
   * `quiet` («без рамки, у щільних таблицях»).
   */
  const paint = quiet
    ? ({ variant: 'transparent' } as const)
    : ({ variant: 'default', bg: fill.bg } as const);

  /*
   * ⛔ `miw="fit-content"` БЕЗУМОВНО, а не пропом сторінки. Дефект виміряний у
   * живому браузері (UI-аудит, lane 8): `table-layout: auto` бере ширину
   * стовпця з того, що РЕНДЕРИТЬСЯ, а власний `overflow:hidden` у
   * `.mantine-Badge-label` дозволяє бейджу «поміститись» у будь-яку ширину —
   * тож на ~554px «Scheduled» ставало нечитабельним «S…» замість того, щоб
   * увімкнути горизонтальну прокрутку (`clientWidth` мітки 9px проти
   * `scrollWidth` 65px; з цим стилем обидва збігаються).
   *
   * ⚠ Прапорець на виклику розглянуто і відкинуто: сторінка, яка МУСИТЬ
   * пам'ятати цей проп, — це рівно та розбіжність між екранами, заради
   * усунення якої набір і заведено. Той самий дефект уже довелося ловити
   * двічі окремо (`PeriodsPage.stateBadgeMinWidth`,
   * `SnapshotsPage.statusBadgeMinWidth`), тобто наступна таблиця забула б
   * його втретє. Обрізаний до однієї літери статус не буває бажаним: підпис —
   * другий носій змісту (`ФВ-14.18`), і без нього лишається сам колір.
   */
  return (
    <Badge
      size="sm"
      miw="fit-content"
      c={fill.text}
      title={title}
      data-status-kind={kind}
      data-status-state={state}
      data-status-tone={tone}
      data-status-known={String(isKnownStatus(kind, state))}
      {...paint}
    >
      {t(statusKey(kind, state))}
    </Badge>
  );
}
