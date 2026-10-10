import { useEffect, useSyncExternalStore } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { showApiError } from '@/shared/ui/notify';
import { registerUnsavedSource, UnsavedSettleMs } from '@/shared/ui/unsavedSources';
import { EcrApiError, isSessionClosed, onBeforeLoginRedirect } from '@/api/client';
import { recordLostEdits } from './lostEdits';
import { resetConfirmed } from './confirmedEdits';
import { registerHeldEditLookup } from './settleEdits';
import {
  cellKey,
  discardPendingRows,
  firstHeldEdit,
  hasPending,
  hasSendablePending,
  markPendingRejected,
  openDocument,
  pendingCount,
  pendingSlices,
  resetPending,
  sendableEdits,
  subscribePending,
  hasFailedSendable,
  noteSaveFailed,
  noteSaveSucceeded,
  resetFailedSaves,
} from './pendingStore';

// ⚠ `G1-03`/`G1-04`: позначки невдалого збереження живуть у сховищі правок (скидаються
// разом із ним, `resetPending`); звідси — для зберігачів і сітки.
export { hasFailedSave, noteSaveSucceeded, resetFailedSaves, useFailedSave } from './pendingStore';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { withKnownVersions } from './edits';
import { rejectionMarksOf } from './saveErrors';
import { beginInFlight, deferUntilInFlightSettles, hasInFlight, splitByInFlight } from './inFlightEdits';
import {
  applyPatchLocally,
  refreshStaleness,
  buildRequest,
  patchCells,
  sendPatchBeacon,
  type PendingEdit,
} from './useCellPatch';

/**
 * Оркестрація автозбереження grid (`B-35`, `#38`).
 *
 * ⛔ `useCellPatch` довгий час ОБІЦЯВ коментарем дебаунс ~500 мс і збереження
 * при закритті вкладки — і жоден із двох механізмів не існував: єдиний
 * спосіб зберегти правку був явний `Ctrl+S` або кнопка «Зберегти». Оператор,
 * що закривав вкладку одразу після правки, втрачав її мовчки: `pending` жив
 * лише в пам'яті компонента.
 *
 * ⚠ Обидва механізми винесені сюди, а не в `DocumentGrid.tsx`, з тієї самої
 * причини, що й `clipboard.ts`/`undo.ts`: таймер і слухач `window` можна
 * перевірити напряму — фальшивими таймерами й подією `beforeunload` — без
 * монтування grid.
 *
 * ✎ `D14-12`, крок 2: до цих двох чистих примітивів додався РІВЕНЬ ДОКУМЕНТА
 * (нижня половина файлу). Дебаунс тепер ОДИН на документ, а не по одному на
 * кожну з 91 сітки, і план збереження більше не належить компонентові, який
 * його завів. Що це лікує — `W-02`: сітка в cleanup робила
 * `autosaveDebouncer.current.cancel()`, і правка молодша за 500 мс зникала
 * МОВЧКИ при перемиканні аркуша. Обидва відомі обходи директива відхиляє
 * прямо: `flush()` у cleanup сітки («результат нікому показати») і заборона
 * перемикати аркуш при `pending > 0` («карає користувача за швидкість»).
 *
 * ⚠ Примітиви лишилися чистими: `createDebouncer` і `registerUnloadFlush` не
 * знають ні про React, ні про сховище, і їх так само перевіряють напряму
 * (`__tests__/autosave.test.ts`).
 */

/** Відкладений виклик: кожен новий `trigger()` скасовує попередній план. */
interface Debouncer {
  /** Планує виклик через `delayMs` тиші; попередній план скасовується. */
  trigger(): void;
  /** Знімає запланований виклик, нічого не викликаючи. */
  cancel(): void;
}

/**
 * Дебаунс автозбереження.
 *
 * ⚠ Саме дебаунс, а не троттлінг: зберігати на КОЖЕН натиск клавіші —
 * сотні запитів на один рядок (див. `buildRequest` у `useCellPatch.ts`), а
 * зберігати за розкладом незалежно від того, чи скінчив оператор редагувати
 * комірку, — це збереження півправки.
 */
export function createDebouncer(callback: () => void, delayMs = 500): Debouncer {
  let handle: ReturnType<typeof setTimeout> | null = null;

  return {
    trigger(): void {
      if (handle !== null) clearTimeout(handle);
      handle = setTimeout(() => {
        handle = null;
        callback();
      }, delayMs);
    },
    cancel(): void {
      if (handle !== null) clearTimeout(handle);
      handle = null;
    },
  };
}

/**
 * Реєструє останній шанс зберегти незбережені правки перед закриттям вкладки.
 *
 * ⚠ `beforeunload` не чекає на `fetch`: сторінка вивантажується незалежно
 * від того, чи встиг запит дійти. Єдиний надійний спосіб донести дані —
 * запит із `keepalive` (`sendPatchBeacon` у `useCellPatch.ts`), не проміс,
 * на завершення якого тут ніхто не чекає й не може чекати.
 *
 * ⛔ `event.preventDefault()` тут НЕ викликається, і діалог «покинути
 * сторінку?» не з'являється. Мета автозбереження — прибрати саму потребу
 * питати оператора, а не підмінити явне збереження попередженням, яке за
 * звичкою закривають не читаючи.
 *
 * @returns Функція відписки — знімає слухача при розмонтуванні.
 */
export function registerUnloadFlush(isPending: () => boolean, flush: () => boolean | void): () => void {
  const handler = (event: BeforeUnloadEvent): void => {
    // AN-39/L8-08: `flush` повертає `true`, якщо лишилось те, що надіслати не можна
    // (утримані відхилені правки) - тоді єдиний чесний захист - рідне питання браузера.
    if (isPending() && flush() === true) {
      event.preventDefault();
      event.returnValue = '';
    }
  };

  window.addEventListener('beforeunload', handler);

  return () => window.removeEventListener('beforeunload', handler);
}

/* ────────────────────────────────────────────────────────────────────────────
 * Рівень ДОКУМЕНТА (`D14-12`, крок 2).
 * ──────────────────────────────────────────────────────────────────────────── */

/** Зріз із незбереженими правками — рівно те, що віддає `pendingSlices()`. */
type PendingSlice = ReturnType<typeof pendingSlices>[number];

/**
 * Хто вміє зберегти зріз І ПОКАЗАТИ результат — змонтована сітка.
 *
 * ⚠ Саме показ результату, а не надсилання, робить сітку привілейованим
 * зберігачем свого зрізу: відмова валідації, конфлікт `409`, маркери
 * обов'язкових вхідних колонок належать її екрану. Доки вона на місці —
 * зберігає вона.
 */
type SliceSaver = (edits: readonly PendingEdit[]) => void;

/**
 * Хто зберігає зріз, чиєї сітки вже НЕМА на екрані.
 *
 * ⛔ Це і є відповідь на `W-02`, і вона не збігається з жодним із двох
 * відхилених директивою варіантів: правка не гине разом із компонентом, але й
 * не надсилається від його імені — її бере документ, який живий.
 */
type OrphanSaver = (slice: PendingSlice) => void;

const savers = new Map<string, SliceSaver>();
let orphanSaver: OrphanSaver | null = null;

function keyOfSlice(tableInstanceId: number, periodKey: number): string {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

/**
 * Оголошує сітку зберігачем свого зрізу на час, доки вона змонтована.
 *
 * @returns Відписка; після неї зріз стає безхазяйним і його бере документ.
 */
export function registerSliceSaver(
  tableInstanceId: number,
  periodKey: number,
  save: SliceSaver,
): () => void {
  const key = keyOfSlice(tableInstanceId, periodKey);
  savers.set(key, save);

  return () => {
    // ⚠ Лише СВІЙ запис: сітка того самого зрізу могла змонтуватися наново
    // (прокрутка, зміна ключа) ДО того, як відпрацював цей cleanup, — React
    // не гарантує порядку. Безумовний `delete` прибрав би зберігача живої
    // сітки, і її зріз мовчки перейшов би на шлях безхазяйного.
    if (savers.get(key) === save) savers.delete(key);
  };
}

/** Ставить зберігача безхазяйних зрізів; повертає зняття. */
function installOrphanSaver(save: OrphanSaver): () => void {
  orphanSaver = save;

  return () => {
    if (orphanSaver === save) orphanSaver = null;
  };
}

/**
 * Дебаунс автозбереження — ОДИН на документ.
 *
 * ⛔ Раніше дебаунсер жив у кожній сітці (`autosaveDebouncer` у
 * `DocumentGrid.tsx`) і вмирав разом із нею. Саме тому перемикання аркуша
 * коштувало правки: план збереження належав компонентові, а не даним.
 *
 * ⚠ Модульний, а не React-стан: рівно з тієї ж причини, що й саме сховище
 * (`pendingStore.ts`) — план мусить пережити розмонтування того, хто його
 * завів.
 */
const scheduler = createDebouncer(() => {
  flushAutosave();
});

/** Планує збереження ВСІХ незбережених зрізів документа через 500 мс тиші. */
export function scheduleAutosave(): void {
  scheduler.trigger();
}

/** Знімає план, нічого не зберігаючи (вихід із документа, тести). */
export function cancelAutosave(): void {
  scheduler.cancel();
}

/**
 * Зберігає все незбережене НЕГАЙНО: кожен зріз — своїм зберігачем.
 *
 * ⚠ Обхід іде по СХОВИЩУ, а не по змонтованих сітках: зріз, чия сітка зникла
 * з екрана, у переліку однаково є — і саме він потрапляє до `orphanSaver`.
 */
function flushAutosave(): void {
  scheduler.cancel();

  // ⛔ `V-01`: лише те, що не тримає відмова сервера. Відхилена правка в пакеті
  // — це пакет, відхилений цілком, разом з усіма правильними правками.
  for (const slice of pendingSlices({ sendableOnly: true })) {
    const saver = savers.get(keyOfSlice(slice.tableInstanceId, slice.periodKey));

    if (saver !== undefined) {
      saver(slice.edits);
      continue;
    }

    // ⚠ Зберігача безхазяйних ставить `useDocumentPending` — тобто
    // `DocumentPage`. Його відсутність означає, що документа на екрані немає
    // взагалі (компонентний тест самої сітки): слати нікуди й нема від чийого
    // імені, і правка лишається в сховищі, а не зникає.
    orphanSaver?.(slice);
  }
}

/* ────────────────────────────────────────────────────────────────────────────
 * Повтор після «дані зайняті» (AN-123, `R1-03`/`R2-01`).
 * ──────────────────────────────────────────────────────────────────────────── */

/**
 * Відступи повтору після `409 ECR-DOC-4091 lockTimeout`, мс: 5 → 10 → 20 → 30
 * (далі 30). Кожен множиться на розкид `[0.5, 1)`.
 *
 * ⚠ Саме відступ, а не негайний повтор: блокування тримає довга операція
 * (перенос версії — хвилини), і кожен повтор займає з'єднання сервера на 15 с
 * очікування. Без розкиду сотня вкладок, що впали разом, разом і повторювала б.
 */
export const BusyRetryDelaysMs: readonly number[] = [5_000, 10_000, 20_000, 30_000];

/**
 * Скільки разів поспіль автозбереження саме повторює `503 ECR-SYS-0503 databaseBusy`,
 * перш ніж передати справу людині (R7-Y8 / Y8-02).
 *
 * ⛔ Межа — лише для `503`, не для `409 lockTimeout` (AN-123): блокування тримає ІНША
 * операція, і вона закінчиться. `503` сервер дає і на тайм-аут команди (`-2`,
 * `ExceptionHandlingMiddleware.TransientSqlNumbers`), а тайм-аут буває детермінованим
 * для САМОГО запиту (велика вставка на піку) — тоді повтор нічого не лікує, лише
 * годинами тримає блокування рядків і з'єднання пулу під нейтральним «чекає».
 * Після межі зріз стає «збереження не дійшло» (`noteSaveFailed`): «Retry save»,
 * позначка відмови й питання при закритті вкладки. Правки не губляться.
 *
 * ⚠ 5 спроб із відступами 5 → 10 → 20 → 30 → 30 с — понад півтори хвилини: минущий
 * збій (перемикання вузла, обрив пулу) за цей час минає.
 */
export const DatabaseBusyAutoRetries = 5;

let busyAttempt = 0;
/** Поспіль отримані `503 databaseBusy` (окремо від `busyAttempt`: `409 lockTimeout` межі не має). */
let databaseBusyAttempt = 0;
let busyTimer: ReturnType<typeof setTimeout> | null = null;
const busyListeners = new Set<() => void>();

function notifyBusy(): void {
  for (const listener of busyListeners) listener();
}

/** Чи чекає документ на повтор після «дані зайняті» (ненав'язливий стан для людини). */
export function isBusyRetryWaiting(): boolean {
  return busyTimer !== null;
}

/**
 * Планує ОДИН повтор автозбереження з відступом після `lockTimeout`.
 *
 * ⚠ Один план на документ: кілька зрізів, що впали в тому самому вікні, не
 * множать повтори — `flushAutosave` везе всі зрізи разом.
 *
 * @param retryAfterSeconds `Retry-After` сервера, якщо він його назвав: нижня межа відступу.
 */
export function scheduleBusyRetry(retryAfterSeconds?: number): void {
  if (busyTimer !== null) return;

  const base = BusyRetryDelaysMs[Math.min(busyAttempt, BusyRetryDelaysMs.length - 1)] ?? 30_000;
  const jittered = base * (0.5 + Math.random() * 0.5);
  const delay = Math.max(jittered, (retryAfterSeconds ?? 0) * 1000);

  busyAttempt += 1;
  busyTimer = setTimeout(() => {
    busyTimer = null;
    notifyBusy();
    flushAutosave();
  }, delay);
  notifyBusy();
}

/** Знімає план повтору й скидає відступ: збереження пройшло або документ закрито. */
export function clearBusyRetry(): void {
  const wasWaiting = busyTimer !== null || busyAttempt > 0;

  if (busyTimer !== null) clearTimeout(busyTimer);
  busyTimer = null;
  busyAttempt = 0;
  databaseBusyAttempt = 0;

  if (wasWaiting) notifyBusy();
}

function subscribeBusy(listener: () => void): () => void {
  busyListeners.add(listener);

  return () => {
    busyListeners.delete(listener);
  };
}

/** Стан «чекає, доки дані звільняться» — для індикатора сітки. */
export function useBusyRetryWaiting(): boolean {
  return useSyncExternalStore(subscribeBusy, isBusyRetryWaiting, isBusyRetryWaiting);
}

/**
 * Скільки байтів тіл може везти маячок закриття вкладки (`G1-03`).
 *
 * ⛔ Специфікація Fetch обмежує суму тіл `keepalive`-запитів документа 64 КиБ;
 * більший запит одразу падає мережевою помилкою, яку маячок ковтає. Тобто
 * вставка на пару тисяч комірок, що чекала повтору, мовчки не доїжджала. Межа
 * — із запасом на заголовки й округлення; більше — рідне питання браузера.
 */
export const BeaconBudgetBytes = 60 * 1024;

/**
 * Тримає відхилені правки пакета й довозить решту (`V-01`).
 *
 * ⛔ Спільне для обох зберігачів — сітки й безхазяйного зрізу: розійдись вони,
 * і правило «відмова не блокує інших» діяло б лише для змонтованої сітки.
 *
 * ⚠ Повтор ПЛАНУЄТЬСЯ, якщо в пакеті були правки, яких відмова не стосувалась:
 * вони правильні, але сервер відхилив їх разом із поганою, і без повтору вони
 * чекали б наступної правки оператора — або перезавантаження, тобто зникли б.
 * Зациклення немає: кожна така відмова позначає щонайменше одну правку
 * (`rejectionMarksOf`), тож наступний пакет щоразу менший.
 *
 * @returns Чи позначено бодай одну правку.
 */
export function holdRejectedEdits(
  tableInstanceId: number,
  periodKey: number,
  error: unknown,
  attempted: readonly PendingEdit[],
): boolean {
  // ⛔ AN-123 (`R1-03`/`R2-01`): «дані зайняті» нічого не тримає — правки лишаються
  // придатними до надсилання (і до маячка закриття вкладки), а повтор іде сам.
  if (error instanceof EcrApiError && error.isTransientBusy) {
    // ⛔ R7-Y8 / Y8-02: `503 databaseBusy` — не безмежно. Після межі план повтору
    // знято, а зріз позначено «збереження не дійшло»: людина бачить відмову й
    // «Retry save» замість вічного «чекає». Правки лишаються придатними до надсилання.
    if (error.problem.errorCode === 'ECR-SYS-0503') {
      databaseBusyAttempt += 1;

      // ⚠ Чужого плану (повтор `409 lockTimeout` іншого зрізу) не знімаємо: він довезе
      // і цей зріз, а відмова повториться тим самим шляхом.
      if (databaseBusyAttempt > DatabaseBusyAutoRetries) {
        noteSaveFailed(tableInstanceId, periodKey);

        return false;
      }
    }

    scheduleBusyRetry(error.problem.retryAfterSeconds);

    return false;
  }

  // ⛔ `G1-03`: відмова, яка нічого не утримала (мережа, `5xx`, `4xx` без
  // позначок), лишає правки «придатними до надсилання» — і закриття вкладки має
  // про них спитати, а не довіряти маячку.
  const marks = rejectionMarksOf(error, attempted);
  if (marks.length === 0) {
    noteSaveFailed(tableInstanceId, periodKey);

    return false;
  }

  if (markPendingRejected(tableInstanceId, periodKey, marks) === 0) {
    noteSaveFailed(tableInstanceId, periodKey);

    return false;
  }

  const sent = new Set(attempted.map((edit) => cellKey(edit)));
  const innocent = sendableEdits(tableInstanceId, periodKey).some((edit) => sent.has(cellKey(edit)));
  if (innocent) scheduleAutosave();

  return true;
}

/**
 * Скільки чекати, доки збереження ВІДСТОЇТЬСЯ (`D14-12`, крок 3).
 *
 * ⚠ Це не дебаунс: його `flushAutosave()` уже зняв. Це рівно час на оберт до
 * сервера й назад — тому й три секунди, а не півтори: діалог «не вдалося
 * зберегти» на повільній мережі, де насправді все збережеться, навчає
 * користувача тиснути «вийти» не читаючи, тобто ламає саме те, заради чого
 * діалог існує.
 */
const AutosaveSettleMs = UnsavedSettleMs;

/**
 * Зберігає все незбережене і чекає на результат.
 *
 * ⛔ Ознака успіху — ПОРОЖНЄ сховище, а не відповідь сервера, і це свідомо.
 * Зберігачем зрізу лишається сітка (`registerSliceSaver`), бо саме їй показувати
 * відмову валідації й конфлікти; її `save()` нічого не повертає й повертати не
 * може — контракт зберігача синхронний. Натомість УСПІХ у цій системі має один
 * спільний слід: підтверджені рядки зникають зі сховища (`discardPendingRows`),
 * а відмова лишає їх на місці. Тобто чекати доводиться не на проміс, а на факт.
 *
 * ⚠ Звідси й таймаут: «правки ще на місці» — це і «сервер відмовив», і «запит
 * ще летить», і розрізнити їх ззовні нічим. Після `timeoutMs` мовчання ми
 * називаємо це невдачею — і це безпечний бік помилки: користувач лишається
 * там, де його дані, а не йде з ними в нікуди.
 *
 * @returns `true` — незбереженого не лишилось; `false` — лишилось.
 */
async function flushAutosaveAndSettle(
  timeoutMs: number = AutosaveSettleMs,
): Promise<boolean> {
  flushAutosave();

  // Зберігач міг відпрацювати синхронно (тест, кеш) — чекати нема на що.
  if (!hasPending()) return true;

  // ⛔ AN-28 P2-1: лишились САМІ утримані відмовою правки — їх не везе ніхто
  // (`V-01`), і «порожнього сховища» не буде ніколи. Раніше тут чекали весь
  // таймаут (3 с) мовчки, а результат був той самий: `false`.
  if (!hasSendablePending()) return false;

  return await new Promise<boolean>((resolve) => {
    let unsubscribe: (() => void) | null = null;

    const stop = (): void => {
      unsubscribe?.();
      unsubscribe = null;
    };

    const timer = setTimeout(() => {
      stop();
      resolve(false);
    }, timeoutMs);

    // ⚠ Відстоялось, коли вже нема чого ВЕЗТИ: решта (якщо є) — утримані
    // відмовою правки, і чекати на них далі означало б мовчати до таймауту.
    const settle = (): void => {
      if (hasSendablePending()) return;

      clearTimeout(timer);
      stop();
      resolve(!hasPending());
    };

    unsubscribe = subscribePending(settle);

    // ⚠ Ще одна перевірка ПІСЛЯ підписки: між `hasPending()` вище і цим рядком
    // синхронний зберігач міг спорожнити сховище, і його сповіщення пройшло б
    // повз ще не встановленого слухача — обіцянка не розв'язалася б ніколи.
    settle();
  });
}

/**
 * Сітка оголошує себе джерелом незбережених змін для `UnsavedGuard`.
 *
 * ⚠ На рівні модуля, а не в монтуванні сітки: сховище правок — модульне й
 * переживає розмонтування сітки (`pendingStore.ts`), тож і джерело мусить
 * жити стільки ж. Модуль підвантажується разом із будь-якою сторінкою, що
 * вміє створити правку, — раніше правок бути не може.
 */
registerUnsavedSource('grid', {
  hasUnsaved: hasPending,
  unsavedCount: pendingCount,
  flush: flushAutosaveAndSettle,
});

// AN-28 P2-1: дії документа називають утриману комірку - з того самого сховища.
registerHeldEditLookup(firstHeldEdit);

/**
 * Зберігає безхазяйний зріз від імені ДОКУМЕНТА.
 *
 * ⚠ Результат: успіх — правки зникають зі сховища й лягають у кеш зрізу, тож
 * повернення на аркуш показує введене; відмова — тост (`showApiError`), бо
 * банера сітки, якому вона адресована, на екрані вже немає. Спільний банер
 * документа — наступний крок `D14-12` (`saveError`/`conflicts` на рівні
 * документа); доки його немає, мовчати про відмову не можна взагалі.
 */
async function saveOrphanSlice(
  queryClient: QueryClient,
  documentId: number,
  slice: PendingSlice,
): Promise<void> {
  // ⛔ AN-104 (`D1-01`): рядок, чий запит уже летить, вдруге не шлемо — версія в
  // кеші ще стара, і сховище відповіло б `409` на власну правку. Доти цей шлях
  // не мав навіть дедуплікації за значенням. Відкладене повторить автозбереження,
  // щойно звільниться запит у дорозі.
  const { now: edits, deferred } = splitByInFlight(slice.tableInstanceId, slice.periodKey, slice.edits);

  if (deferred.length > 0) deferUntilInFlightSettles(scheduleAutosave);

  if (edits.length === 0) return;

  // ⛔ `B-09`: версія рядка — остання відома кешу, а не та, з якою правку
  // зроблено (`withKnownVersions`): інакше правка, що чекала повтору, їхала б
  // зі старою версією й діставала `409` на власних змінах.
  const request = buildRequest(
    slice.tableInstanceId,
    slice.periodKey,
    withKnownVersions(edits, cachedSlice(queryClient, slice.tableInstanceId, slice.periodKey)),
  );

  // ⚠ Знімок ТОГО, ЩО ПІШЛО: доки patch летить, у той самий зріз може
  // прийти нова правка з іншої сітки чи з відновленої черги — і підтверджувати
  // «усе, що було в рядку» означало б стерти значення, якого сервер не бачив.
  const sent = new Map(edits.map((edit) => [cellKey(edit), edit]));
  const endInFlight = beginInFlight(slice.tableInstanceId, slice.periodKey, edits);

  try {
    const response = await patchCells(documentId, request);

    applyPatchLocally(queryClient, request, response);
    refreshStaleness(queryClient, documentId, request.periodKey);
    discardPendingRows(
      slice.tableInstanceId,
      slice.periodKey,
      edits.map((edit) => edit.rowKey),
      sent,
    );
    noteSaveSucceeded(slice.tableInstanceId, slice.periodKey);
    clearBusyRetry();
  } catch (error) {
    holdRejectedEdits(slice.tableInstanceId, slice.periodKey, error, edits);
    // ⚠ AN-123: «дані зайняті» — не тост: повтор уже заплановано, а стан «чекає»
    // показує індикатор (`useBusyRetryWaiting`).
    if (!(error instanceof EcrApiError && error.isTransientBusy)) showApiError(error);
  } finally {
    endInFlight();
  }
}

/**
 * Незбережені правки на рівні ДОКУМЕНТА: відкриття сховища, збереження
 * безхазяйних зрізів і останній шанс перед закриттям вкладки.
 *
 * ⛔ Хук живе тут, а не в `DocumentPage.tsx`, з двох причин. Перша: сторінка
 * не має права статично тягнути нічого з ядра сітки (`RevoGrid` — 79,1 %
 * чанка маршруту, `D-132`), а цей модуль ядра не торкається взагалі — лише
 * сховища й `useCellPatch`. Друга: рівно цю композицію («документ + сітки»)
 * має відтворювати доказ `D14-12`, і відтворювати її він мусить тим самим
 * кодом, що й продукт, а не переписуванням ефектів у тесті.
 *
 * ⛔ Вихід зі сторінки СКИДАЄ сховище — те саме, що робила попередня схема,
 * лише тепер в одному місці. Перехопити вихід і спершу зберегти (`useBlocker`,
 * діалог «є незбережені зміни») — крок 3 `D14-12`; цей хук навмисно не
 * вигадує для нього половинчастого рішення.
 */
export function useDocumentPending(documentId: number, ownerUserId?: number): void {
  const queryClient = useQueryClient();

  // ⚠ `401` перезавантажує сторінку, і правки зникають разом із пам'яттю:
  // зберегти їх уже нема чим, тож лишаємо слід для сторінки входу
  // (`lostEdits.ts`), прив'язаний до власника.
  useEffect(
    () => onBeforeLoginRedirect((from) => recordLostEdits(ownerUserId, from)),
    [ownerUserId],
  );

  useEffect(() => {
    openDocument(documentId);

    return () => {
      cancelAutosave();
      clearBusyRetry();
      resetFailedSaves();
      resetPending();
      // ⚠ Разом зі сховищем правок — і підтвердження до них (`ФВ-2.16`).
      resetConfirmed();
    };
  }, [documentId]);

  useEffect(
    () =>
      installOrphanSaver((slice) => {
        void saveOrphanSlice(queryClient, documentId, slice);
      }),
    [documentId, queryClient],
  );

  // ⚠ Останній шанс перед закриттям вкладки — теж НА ДОКУМЕНТ, а не на сітку:
  // доки слухач реєструвала кожна сітка окремо, `beforeunload` віз правки лише
  // тієї з них, у якій стояв курсор (`W-02`, друге речення). Тепер їдуть усі
  // зрізи сховища, включно з тими, чиї сітки давно розмонтовані прокруткою.
  useEffect(
    () =>
      registerUnloadFlush(hasPending, () => {
        // ⛔ AN-108 / S2-05: сеанс змінився в іншій вкладці — правки вже лежать у сліді `lostEdits` їхнього
        // власника, а відправити їх зараз означало б записати їх під чужим cookie. Без маячка і без питання.
        if (isSessionClosed()) return false;

        // ⚠ `V-01`: і тут без відхилених — інакше останній шанс зберегти
        // правильні правки згорів би на тій самій відмові.
        const sendable = pendingSlices({ sendableOnly: true });

        // AN-39/L8-08: відхилені (утримані) правки надіслати неможливо - про них
        // питаємо рідним питанням браузера.
        const held = pendingCount() > sendable.reduce((sum, slice) => sum + slice.edits.length, 0);

        // ⛔ L8-08 (PARTIAL): за наявності утриманих правок маячок НЕ шлемо. Питання
        // «Покинути сторінку?» з'являється вже ПІСЛЯ обробника; якщо людина натисне
        // «Залишитися», правильні правки, що поїхали маячком, лишилися б у сховищі зі старим
        // `baseVersion` і 409 прийшов би на власні правки. Тому: звичайне збереження
        // ПІСЛЯ обробника (`setTimeout 0`) — воно оновить версії; обрала «Піти» — вкладка
        // закриється, а питання людина вже бачила.
        //
        // ⛔ AN-104 (`D1-03`): те саме, коли збереження ще В ДОРОЗІ. Маячок віз би
        // комірки, що вже летять, зі старою версією кешу: відповідь першого запиту
        // її ще не підняла, і `409` на маячок («все або нічого») забрав би з собою
        // новіші правки, мовчки (`lostEdits` пишеться лише на `401`). Тож — рідне
        // питання браузера; «Залишитися» — звичайне збереження після відповіді.
        //
        // ⛔ `G1-03`: так само, коли маячок НЕ доїде: «дані зайняті» (повтор
        // чекає), останнє збереження впало мережею чи `5xx` (маячок пішов би
        // до того самого недоступного сервера), або пакет більший за ліміт
        // `keepalive` (64 КиБ — запит падає одразу, мовчки).
        const requests = sendable.map((slice) =>
          buildRequest(
            slice.tableInstanceId,
            slice.periodKey,
            withKnownVersions(slice.edits, cachedSlice(queryClient, slice.tableInstanceId, slice.periodKey)),
          ),
        );
        const tooBig = beaconBytes(requests) > BeaconBudgetBytes;

        if (held || hasInFlight() || isBusyRetryWaiting() || hasFailedSendable() || tooBig) {
          setTimeout(() => {
            flushAutosave();
          }, 0);

          return true;
        }

        for (const request of requests) sendPatchBeacon(documentId, request);

        return false;
      }),
    [documentId, queryClient],
  );
}

/** Сума тіл маячка в байтах UTF-8 — так, як їх рахує ліміт `keepalive`. */
function beaconBytes(requests: readonly unknown[]): number {
  const encoder = new TextEncoder();

  return requests.reduce<number>((sum, request) => sum + encoder.encode(JSON.stringify(request)).length, 0);
}

/** Зріз із кешу — без запиту; `undefined`, якщо його ще не читали. */
function cachedSlice(
  queryClient: QueryClient,
  tableInstanceId: number,
  periodKey: number,
): TableSliceDto | undefined {
  return queryClient.getQueryData<TableSliceDto>(queryKeys.slices.one(tableInstanceId, periodKey));
}
