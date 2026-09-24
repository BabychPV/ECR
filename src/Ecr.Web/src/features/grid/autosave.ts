import { useEffect } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { showApiError } from '@/shared/ui/notify';
import { registerUnsavedSource, UnsavedSettleMs } from '@/shared/ui/unsavedSources';
import { onBeforeLoginRedirect } from '@/api/client';
import { recordLostEdits } from './lostEdits';
import {
  cellKey,
  discardPendingRows,
  hasPending,
  markPendingRejected,
  openDocument,
  pendingCount,
  pendingSlices,
  resetPending,
  sendableEdits,
  subscribePending,
} from './pendingStore';
import { rejectionMarksOf } from './saveErrors';
import {
  applyPatchLocally,
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
export interface Debouncer {
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
export function registerUnloadFlush(isPending: () => boolean, flush: () => void): () => void {
  const handler = (): void => {
    if (isPending()) flush();
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
export type SliceSaver = (edits: readonly PendingEdit[]) => void;

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
export function installOrphanSaver(save: OrphanSaver): () => void {
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
export function flushAutosave(): void {
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
  const marks = rejectionMarksOf(error, attempted);
  if (marks.length === 0) return false;

  if (markPendingRejected(tableInstanceId, periodKey, marks) === 0) return false;

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
export const AutosaveSettleMs = UnsavedSettleMs;

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
export async function flushAutosaveAndSettle(
  timeoutMs: number = AutosaveSettleMs,
): Promise<boolean> {
  flushAutosave();

  // Зберігач міг відпрацювати синхронно (тест, кеш) — чекати нема на що.
  if (!hasPending()) return true;

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

    const settle = (): void => {
      if (hasPending()) return;

      clearTimeout(timer);
      stop();
      resolve(true);
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
  const request = buildRequest(slice.tableInstanceId, slice.periodKey, slice.edits);

  // ⚠ Знімок ТОГО, ЩО ПІШЛО: доки patch летить, у той самий зріз може
  // прийти нова правка з іншої сітки чи з відновленої черги — і підтверджувати
  // «усе, що було в рядку» означало б стерти значення, якого сервер не бачив.
  const sent = new Map(slice.edits.map((edit) => [cellKey(edit), edit]));

  try {
    const response = await patchCells(documentId, request);

    applyPatchLocally(queryClient, request, response);
    discardPendingRows(
      slice.tableInstanceId,
      slice.periodKey,
      slice.edits.map((edit) => edit.rowKey),
      sent,
    );
  } catch (error) {
    holdRejectedEdits(slice.tableInstanceId, slice.periodKey, error, slice.edits);
    showApiError(error);
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
      resetPending();
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
        // ⚠ `V-01`: і тут без відхилених — інакше останній шанс зберегти
        // правильні правки згорів би на тій самій відмові.
        for (const slice of pendingSlices({ sendableOnly: true })) {
          sendPatchBeacon(
            documentId,
            buildRequest(slice.tableInstanceId, slice.periodKey, slice.edits),
          );
        }
      }),
    [documentId],
  );
}
