import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { CellConflictDto, JobStatus, PatchCellsRequest, PatchCellsResponse } from '@/api/types';
import { MoreConflictsExtension } from '@/api/types';
// ⚠ Правило «доки опитувати» береться з наявного модуля, а не пишеться вдруге:
// `jobFollow.ts` уже знає, які стани кінцеві, і саме він виник із двох копій
// цього правила, що розійшлися (`Q-234`). Тут інший лише ІНТЕРВАЛ — див.
// `RecalculationPollMs`.
import { outcomeOf, pollInterval, type JobOutcome } from '@/features/workflow/jobFollow';
import { formatTime } from '@/shared/format';
import { invalidateSlices } from './sliceCache';
import { applyPatchLocally, patchCells, refreshStaleness } from './cellPatchRequest';

// Запит патча й застосування до кешу — у `cellPatchRequest.ts` (`D-132`); реекспорт для сітки й тестів.
export {
  applyPatchLocally,
  buildRequest,
  patchCells,
  refreshStaleness,
  sendPatchBeacon,
  type PendingEdit,
} from './cellPatchRequest';

/**
 * Задачі перерахунку, вже враховані в зрізах (N-1): одна задача - одна інвалідація. `'skipped'` - задача нічого
 * не записала (AN-108 / P2-02) і зрізів не чіпала; пізніше «невідомо що записано» по ній ще перечитає зрізи.
 */
const settledJobs = new Map<string, 'refetched' | 'skipped'>();

/**
 * Скільки задач пам'ятаємо як враховані (P2-07). ⛔ Карта росла на кожну задачу перерахунку весь вік вкладки
 * (оператор тримає її відкритою днями, правка = задача). Повтор рішення потрібен лише для недавніх задач
 * (сітка і слідкувач доганяють одну й ту саму), тож старіші забуваємо — за порядком вставлення.
 */
const SettledJobsLimit = 256;

/**
 * Задачі перерахунку документа за період, поставлені правками і ще не враховані в зрізах (AN-108 / P2-02),
 * з порядковим номером постановки. Слідкувач переходить на новішу задачу, не дочекавшись старішої, тож
 * «остання нічого не записала» ще не означає «нічого не записано»: записане старішою знає лише вона.
 */
const pendingRecalculations = new Map<string, Map<string, number>>();
let recalculationSeq = 0;

/** Запам'ятовує задачу перерахунку, поставлену правкою (AN-108 / P2-02). */
function registerRecalculation(jobId: string, documentId: number, periodKey: number): void {
  const key = `${documentId}:${periodKey}`;
  const jobs = pendingRecalculations.get(key) ?? new Map<string, number>();
  if (!jobs.has(jobId)) jobs.set(jobId, (recalculationSeq += 1));
  pendingRecalculations.set(key, jobs);
}

/**
 * Прибирає задачу і всі СТАРІШІ за неї з очікуваних; повертає, чи серед старіших була хоч одна - тобто
 * чи лишився невідомий запис, який мусить перекрити інвалідація цієї.
 */
function takeRecalculation(jobId: string, documentId: number, periodKey: number): boolean {
  const key = `${documentId}:${periodKey}`;
  const jobs = pendingRecalculations.get(key);
  if (jobs === undefined) return false;
  const own = jobs.get(jobId);
  let olderPending = false;
  for (const [id, seq] of [...jobs]) {
    if (id === jobId || (own !== undefined && seq < own)) {
      if (id !== jobId) olderPending = true;
      jobs.delete(id);
    }
  }
  if (jobs.size === 0) pendingRecalculations.delete(key);
  return olderPending;
}

/**
 * N-1 (RC15): скидає зрізи документа за період ПІСЛЯ завершення перерахунку - один раз на задачу.
 * Повертає `false`, якщо цю задачу вже оброблено (сітка і фоновий слідкувач не дублюють запити).
 *
 * ⛔ AN-108 / P2-02: `writtenCount === 0` (сервер: перерахунок нічого не записав) - зрізи не чіпаються: власна
 * правка вже в кеші (`applyPatchLocally`), а перезапит усіх змонтованих зрізів (до 91) на кожне
 * автозбереження - найдорожчий наслідок PATCH. `null`/`undefined` (невідомо, старий сервер) - як раніше.
 * Невраховані старіші задачі того самого документа (слідкувач перейшов з них на цю) теж змушують перечитати.
 *
 * @param writtenCount `JobStatus.writtenCount` завершеної задачі.
 */
export function settleRecalculation(
  queryClient: QueryClient,
  jobId: string,
  documentId: number,
  periodKey: number,
  writtenCount?: number | null,
): boolean {
  const previous = settledJobs.get(jobId);
  if (previous === 'refetched') return false;
  const olderPending = takeRecalculation(jobId, documentId, periodKey);
  const nothingWritten = writtenCount === 0 && !olderPending;
  // Уже враховано як «нічого не записала», і знову нічого нового - повтор ігнорується.
  if (previous === 'skipped' && nothingWritten) return false;
  // ⚠ Видалення перед записом — щоб оновлена задача стала найсвіжішою в порядку вставлення.
  settledJobs.delete(jobId);
  settledJobs.set(jobId, nothingWritten ? 'skipped' : 'refetched');
  while (settledJobs.size > SettledJobsLimit) {
    const oldest = settledJobs.keys().next();
    if (oldest.done === true) break;
    settledJobs.delete(oldest.value);
  }
  if (!nothingWritten) void invalidateSlices(queryClient, { documentId, periodKey });
  return true;
}

/** Скільки разів фоновий слідкувач питає про задачу (2 с * 60 = 2 хв). */
const FollowMaxPolls = 60;

/**
 * Загальна стеля життя слідкувача від першої задачі (AN-108 / P2-03): `attempt` скидається на кожну нову задачу,
 * тож за безперервного введення стеля спроб сама по собі не спрацьовувала ніколи.
 */
const FollowMaxMs = 10 * 60_000;

/** Активні слідкувачі: ключ `документ:період` -> остання задача, за якою він стежить. */
const activeFollowers = new Map<string, { jobId: string }>();

/**
 * Чи стежить за задачею фоновий слідкувач (AN-108 / P2-03). Тоді сітка лише ЧИТАЄ кеш `['job', id]`, який
 * наповнює слідкувач, а не опитує той самий стан другим незалежним таймером.
 */
export function isFollowedJob(jobId: string): boolean {
  for (const follower of activeFollowers.values()) {
    if (follower.jobId === jobId) return true;
  }
  return false;
}

/** Чи прихована вкладка. */
function isHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/** Чекає, доки вкладка знову стане видимою (AN-108 / P2-03): прихована вкладка не опитує сервер. */
function untilVisible(): Promise<void> {
  return new Promise((resolve) => {
    const onChange = (): void => {
      if (document.visibilityState === 'hidden') return;
      document.removeEventListener('visibilitychange', onChange);
      resolve();
    };
    document.addEventListener('visibilitychange', onChange);
  });
}

/**
 * Фонове стеження за перерахунком, поставленим правкою (N-1).
 *
 * ⛔ Сітка, що поставила задачу, може розмонтуватися (перехід на іншу вкладку - «Contract»), і тоді її
 * власне стеження зникає разом з нею, а `staleTime` зрізів 5 хв лишає в кеші інших аркушів значення
 * ДО перерахунку (U3=200 замість 180). Слідкувач живе поза компонентом і по завершенні задачі
 * скидає зрізи всього документа за період.
 *
 * ⚠ Один слідкувач на документ і період: швидкі підряд правки не плодять цикли опитування, а лише
 * переводять наявного на НОВУ задачу (старіші перерахунки поглинаються останнім). Інвалідація - одна,
 * після завершення останньої задачі.
 */
export function followRecalculation(
  queryClient: QueryClient,
  jobId: string,
  documentId: number,
  periodKey: number,
): void {
  const key = `${documentId}:${periodKey}`;
  registerRecalculation(jobId, documentId, periodKey);
  const existing = activeFollowers.get(key);
  if (existing) {
    existing.jobId = jobId;
    return;
  }
  const follower = { jobId };
  activeFollowers.set(key, follower);
  void (async () => {
    let polledJob = follower.jobId;
    let settled = false;
    try {
      const startedAt = Date.now();
      let attempt = 0;
      while (attempt < FollowMaxPolls && Date.now() - startedAt < FollowMaxMs) {
        // ⚠ Лише коли прихована: інакше перший запит іде синхронно зі стартом слідкувача, як і раніше.
        if (isHidden()) await untilVisible();
        if (follower.jobId !== polledJob) {
          polledJob = follower.jobId;
          attempt = 0;
        }
        attempt += 1;
        const current = polledJob;
        const job = await queryClient.fetchQuery({
          queryKey: ['job', current],
          queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(current)}`),
          staleTime: 0,
          retry: false,
        });
        // Поки опитували, з'явилась новіша задача: чекаємо на неї, а не скидаємо зрізи передчасно.
        if (follower.jobId !== current) continue;
        if (pollInterval(job.state) === false) {
          settled = true;
          settleRecalculation(queryClient, current, documentId, periodKey, job.writtenCount);
          return;
        }
        await new Promise((resolve) => setTimeout(resolve, RecalculationPollMs));
      }
    } catch {
      // Помилка опитування: слідкувач завершується, сітка (якщо змонтована) підхоплює стеження — див. `finally`.
    } finally {
      if (activeFollowers.get(key) === follower) activeFollowers.delete(key);
      // ⚠ AN-108 / P2-03: поки слідкувач жив, змонтована сітка свого таймера не тримала (`isFollowedJob`). Пішов,
      // не дочекавшись кінця, — один перезапит змусить спостерігача сітки перерахувати інтервал і стежити самому.
      if (!settled) void queryClient.invalidateQueries({ queryKey: ['job', polledJob], exact: true });
    }
  })();
}

/**
 * Видимий стан збереження (`B-35`, `#38`).
 *
 * ⛔ Оператор має бачити, чи дійшла його правка до сервера, а не здогадуватися
 * з відсутності помилки. Мовчазне автозбереження — небезпека, а не зручність:
 * саме так сформульований ризик у розборі `B-35`.
 */
export type SaveStatus = 'idle' | 'saving' | 'saved' | 'error';

/**
 * Хук пакетного збереження комірок.
 *
 * ⚠ Дебаунс ~500 мс і збереження перед закриттям вкладки — це `autosave.ts`
 * (`scheduleAutosave`, `useDocumentPending`), не цей хук: обидва механізми
 * працюють над `pending`-мапою ВСЬОГО документа (`pendingStore.ts`,
 * `D14-12`), а не над мапою однієї сітки, тож і живуть рівнем вище. Хук
 * натомість відповідає за ВИДИМИЙ підсумок: чи зберігається зараз, чи
 * збереглося, чи впало.
 */
export function useCellPatch(documentId: number): {
  patch: (request: PatchCellsRequest) => Promise<PatchCellsResponse>;
  /** Версії рядків після останнього успішного збереження. */
  rowVersions: Record<string, string>;
  isPending: boolean;

  /**
   * Розбіжності останньої відмови `ECR-CELL-0409` (`BE-06`).
   *
   * ⛔ Було `unknown[]` — і саме тому інтерфейс показував лише ЛІЧИЛЬНИК
   * («змінено комірок: 3»). Тип, який нічого не обіцяє, не дає чого показати:
   * щоб намалювати «їхнє значення 12.40 · A. Serikbayev · 14:02», треба знати,
   * що ці поля існують.
   */
  conflicts: CellConflictDto[];

  /**
   * Скільки розбіжних комірок сервер НЕ вмістив у перелік (стеля — 100).
   *
   * ⚠ `0` — усе вмістилося. Мовчати про решту не можна: людина, яка бачить сто
   * рядків із трьохсот, вважає, що бачить усі.
   */
  moreConflicts: number;
  /** Видимий індикатор для оператора — не лише лічильник незбереженого. */
  status: SaveStatus;

  /**
   * Задача перерахунку, поставлена ОСТАННІМ успішним патчем; `null` — стежити
   * нема за чим (`BE-05`).
   *
   * ⚠ Останній, а не перший: кожен наступний патч того самого зрізу ставить
   * СВІЙ перерахунок, і показувати стан уже витісненої задачі означало б
   * повідомити «перераховано» про числа, які відтоді змінилися ще раз.
   */
  recalculationJobId: string | null;
} {
  const queryClient = useQueryClient();
  const [isPending, setPending] = useState(false);
  const [conflicts, setConflicts] = useState<CellConflictDto[]>([]);
  const [moreConflicts, setMoreConflicts] = useState(0);
  const [status, setStatus] = useState<SaveStatus>('idle');
  const [recalculationJobId, setRecalculationJobId] = useState<string | null>(null);
  const savedTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const versions = useRef<Record<string, string>>({});

  const patch = useCallback(
    async (request: PatchCellsRequest): Promise<PatchCellsResponse> => {
      setPending(true);
      setConflicts([]);
      setMoreConflicts(0);
      setStatus('saving');
      if (savedTimer.current !== null) clearTimeout(savedTimer.current);

      try {
        const response = await patchCells(documentId, request);

        // ⚠ Версії оновлюються З ВІДПОВІДІ. Без цього наступний патч піде зі
        // старим `baseVersion` і отримає 409 на власних змінах — конфлікт із
        // самим собою, який неможливо пояснити користувачеві.
        versions.current = { ...versions.current, ...response.rowVersions };

        applyPatchLocally(queryClient, request, response);
        refreshStaleness(queryClient, documentId, request.periodKey);

        // ⚠ `BE-05`: `null` у відповіді означає «перерахунку не поставлено»
        // (гілка відкладання `DAT-05`) — і тоді стеження ЗНІМАЄТЬСЯ, а не
        // лишається на попередній задачі: її результат уже не описує те, що
        // зараз у зрізі. `?? null` навмисно: поле необов'язкове в контракті,
        // тож `undefined` зі старішого сервера має читатися так само.
        setRecalculationJobId(response.recalculationJobId ?? null);
        if (response.recalculationJobId) {
          followRecalculation(queryClient, response.recalculationJobId, documentId, request.periodKey);
        }

        // ⚠ «Збережено» показується ТИМЧАСОВО, а не назавжди: індикатор, який
        // ніколи не гасне, оператор перестає читати за перший же день, і він
        // перестає відповідати на питання «чи зберігся мій щойновведений
        // рядок». Дві секунди — досить, щоб побачити, і мало, щоб набриднути.
        setStatus('saved');
        savedTimer.current = setTimeout(() => setStatus('idle'), 2000);

        return response;
      } catch (error) {
        // ⛔ Конфлікт не «вирішується» мовчазним перезаписом: перелік
        // розбіжностей іде в діалог порівняння, і рішення ухвалює людина.
        if (error instanceof EcrApiError && error.isConflict) {
          setConflicts(error.conflicts as CellConflictDto[]);
          setMoreConflicts(moreConflictsOf(error));
        }

        setStatus('error');
        void documentId;
        throw error;
      } finally {
        setPending(false);
      }
    },
    [documentId, queryClient],
  );

  // ⚠ Таймер живе в `ref`, а не лише всередині `patch`: компонент може
  // розмонтуватися між «збережено» і спливанням двох секунд (перехід на іншу
  // таблицю), і виклик `setStatus` на розмонтованому хуку — попередження
  // React, яке нічого корисного не робить, лише шумить у консолі.
  useEffect(() => () => {
    if (savedTimer.current !== null) clearTimeout(savedTimer.current);
  }, []);

  return {
    patch,
    rowVersions: versions.current,
    isPending,
    conflicts,
    moreConflicts,
    status,
    recalculationJobId,
  };
}

/**
 * Скільки розбіжних комірок сервер не вмістив у перелік (`BE-06`).
 *
 * ⛔ Число приходить РЯДКОМ — така конвенція розширень `ProblemDetails` на
 * сервері (у шаблон каталогу підставляються лише поля типу `string`). Тому
 * `Number(...)`, а не приведення типу: `'37' as number` скомпілювалося б і дало
 * б рядок там, де далі стоїть арифметика.
 *
 * ⚠ Усе, що не розбирається в скінченне число (поля немає, старіший сервер,
 * чужий формат), — це `0`, тобто «про решту нічого не відомо». Показати `NaN`
 * у реченні «і ще N комірок» було б гірше за мовчання.
 */
export function moreConflictsOf(error: EcrApiError): number {
  const raw = error.problem.extensions2?.[MoreConflictsExtension];
  const parsed = Number(raw);

  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}

/**
 * Як часто питати стан перерахунку, поставленого правкою комірки.
 *
 * ⛔ Не `PollMs` із `jobFollow.ts` (1.5 с), і це свідоме розходження, а не
 * недогляд. Ті задачі оператор запускає РУКАМИ і дивиться на кнопку, доки вони
 * йдуть; ця ж ставиться сама на кожне автозбереження — тобто в найгарячішому
 * циклі роботи, де відкрита вкладка опитує фон постійно. Директива називає
 * стелю прямо: інтервал ≥ 2 с.
 *
 * ⚠ Що вважати кінцевим станом — вирішує `pollInterval` із того ж
 * `jobFollow.ts`, а не власна копія переліку станів: саме розходження двох
 * копій цього правила й коштувало `Q-234`.
 */
export const RecalculationPollMs = 2000;

/** Видимий підсумок перерахунку для статус-рядка сітки. */
interface RecalculationStatus {
  /** Стан задачі; `undefined` — відповіді ще немає. */
  state: string | undefined;

  /** Що показати оператору; `null` — стежити нема за чим. */
  outcome: JobOutcome | null;

  /**
   * Коли перерахунок завершився, `ГГ:ХХ`; `null` — ще йде або нема за чим
   * стежити.
   */
  finishedAt: string | null;

  /**
   * Скільки комірок записав завершений перерахунок (`JobStatus.writtenCount`, AN-108 / P2-02);
   * `null` — ще йде або невідомо.
   */
  writtenCount: number | null;
}

/**
 * Стежить за задачею перерахунку, поставленою правкою комірки (`BE-05`).
 *
 * ⛔ Головне, чого тут НЕМАЄ: жодного `invalidateQueries` зрізу. Це прямий
 * припис директиви, і він захищає вже виконану роботу — `CL-01…03` прибрали
 * перезапит найважчого `GET` системи з кожного успішного збереження. Повернути
 * його «лише на час перерахунку» означало б повернути його на кожне
 * автозбереження, бо перерахунок ставиться саме ним.
 *
 * ⚠ Опитування зупиняється на кінцевому стані (`pollInterval` → `false`):
 * нескінченне опитування готової задачі — це запит раз на дві секунди від
 * КОЖНОЇ відкритої вкладки документа, назавжди.
 *
 * ⚠ `retry: false` і `outcomeOf(..., isError)`: відмова читання — це НЕ «ще
 * виконується». Без цього розрізнення статус-рядок показував би
 * «перераховується» вічно на задачі, стан якої просто не віддали (`Q-156`,
 * той самий дефект, від якого `outcomeOf` рятує кнопку експорту).
 */
export function useRecalculationStatus(jobId: string | null): RecalculationStatus {
  /*
   * ⛔ Порожній рядок прирівняний до `null`, і це не перестраховка. Сервер
   * каже «перерахунку не поставлено» саме `null`-ом (`BE-05`), але рядок
   * нульової довжини, який колись міг би приїхати замість нього, зібрав би
   * адресу `/api/v1/jobs/` — тобто ПЕРЕЛІК задач замість стану однієї, під
   * правом `System.ViewHealth`, якого в редактора немає. Замість мовчазного
   * стеження ні за чим оператор побачив би 403 на кожне збереження.
   */
  const active = jobId !== null && jobId.length > 0 ? jobId : null;

  const job = useQuery({
    // ⚠ Ключ той самий за формою, що в решті екранів, які стежать за задачами
    // (`ExportButton`, `SnapshotsPage`): один `jobId` — один запис у кеші,
    // навіть якщо на нього дивляться з двох місць одночасно.
    queryKey: ['job', active],

    // ⚠ `encodeURIComponent` обов'язковий: `jobId` має вигляд
    // `IFormulaRecalculationJob#42`, а `#` в URL ПОЧИНАЄ ФРАГМЕНТ —
    // незакодований він обрізає шлях до `/api/v1/jobs/IFormulaRecalculationJob`,
    // і сервер чесно відповідає 404. Саме на цьому падав крок 17 `smoke.ps1`.
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(active ?? '')}`),
    enabled: active !== null,
    // ⛔ AN-108 / P2-03: за задачею, яку веде фоновий слідкувач (`followRecalculation`), сітка НЕ тримає
    // другого таймера на той самий ключ — лише читає кеш, який той наповнює. Інтервал перераховується на
    // кожне оновлення запиту, тож коли слідкувач піде без кінцевого стану, сітка підхопить опитування сама.
    refetchInterval: (query) =>
      pollInterval(query.state.data?.state) === false || (active !== null && isFollowedJob(active))
        ? false
        : RecalculationPollMs,
    retry: false,
  });

  const outcome = active === null ? null : outcomeOf(job.data?.state, job.isError);

  /*
   * ⚠ Час завершення — КЛІЄНТСЬКИЙ, і це названо вголос: `JobStatus` несе
   * `state`/`percent`/`message`/`error` і не несе жодної позначки часу
   * (`IBackgroundJobScheduler.cs`). Тому «14:02» означає «коли це побачив цей
   * екран», а не «коли воркер закрив прогін». Різниця — один інтервал
   * опитування, і для рядка «перераховано о…» вона не має ціни; вигадувати
   * точніше з наявних даних ніяк.
   */
  /*
   * ⚠ У стані лежить МОМЕНТ, а не готовий рядок. Напис складається при
   * рендері, бо `formatTime` бере мову з каталогу: збережений рядок пережив
   * би перемикання мови і лишився б у записі попередньої.
   */
  const [finishedAt, setFinishedAt] = useState<Date | null>(null);
  const markedFor = useRef<string | null>(null);

  useEffect(() => {
    if (active === null) {
      setFinishedAt(null);
      markedFor.current = null;

      return;
    }

    if (outcome === null || outcome === 'running') return;
    if (markedFor.current === active) return;

    markedFor.current = active;
    setFinishedAt(new Date());
  }, [active, outcome]);

  // ⚠ Нова задача гасить час попередньої НЕГАЙНО, ще до її першої відповіді:
  // інакше «перераховано о 14:02» висіло б поруч із правкою, зробленою о 14:05.
  const shown = markedFor.current === active ? finishedAt : null;

  return {
    state: job.data?.state,
    outcome,
    finishedAt: shown === null ? null : formatTime(shown),
    writtenCount: job.data?.writtenCount ?? null,
  };
}

/**
 * Момент чужої правки, часом інтерфейсу (`BE-06`).
 *
 * ⛔ `null` на вході — це `null` на виході, а не поточний час і не порожній
 * рядок. Сервер каже `null` рівно тоді, коли автора й моменту встановити не
 * вдалося, і підставити тут «зараз» означало б повернути той самий дефект,
 * який `BE-06` і прибирає, — лише на клієнті.
 *
 * ✎ 2026-09-19. Тут і в статус-рядку перерахунку стояв власний `clockLabel`,
 * що складав `ГГ:ХХ` вручну, і його коментар пояснював це так: «`D15-09`
 * забороняє `toLocale*()` без явної локалі, а явної локалі тут узяти ніде:
 * `kz` не є тегом BCP-47, і `Intl` на ньому кидає `RangeError`».
 *
 * ⛔ Перша половина правдива, друга — ні. Місце, де локаль береться, існує і
 * називається `shared/format`: `formatLocale()` зводить мову продукту до
 * чинного тегу (`kz` → `kk`), а `formatTime()` через нього й іде. Що воно НЕ
 * кидає саме на `kz`, доводить `shared/format/__tests__/locale.test.ts`
 * (`не кидає на мові, якої немає в BCP-47`).
 *
 * ⚠ Ціна тієї неправди була видима: `formatTime` під `en` дає `2:05 PM`
 * (`datetime.test.ts`), а `clockLabel` — завжди `14:05`. Тобто на одному
 * екрані англійського інтерфейсу момент чужої правки й момент перерахунку
 * писалися 24-годинним записом, а кожна інша позначка часу — 12-годинним.
 * Рівно те, чого цей самий коментар і застерігався уникати: «два різні
 * написи часу на одному екрані читалися б як два різні поняття».
 */
export function conflictTimeLabel(iso: string | null): string | null {
  if (iso === null) return null;

  const at = new Date(iso);

  return Number.isNaN(at.getTime()) ? null : formatTime(at);
}
