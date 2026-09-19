import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CellConflictDto,
  JobStatus,
  PatchCell,
  PatchCellsRequest,
  PatchCellsResponse,
  TableSliceDto,
} from '@/api/types';
import { MoreConflictsExtension } from '@/api/types';
// ⚠ Правило «доки опитувати» береться з наявного модуля, а не пишеться вдруге:
// `jobFollow.ts` уже знає, які стани кінцеві, і саме він виник із двох копій
// цього правила, що розійшлися (`Q-234`). Тут інший лише ІНТЕРВАЛ — див.
// `RecalculationPollMs`.
import { outcomeOf, pollInterval, type JobOutcome } from '@/features/workflow/jobFollow';
import { formatTime } from '@/shared/format';
import { applyPatchToSlice } from './sliceApply';

/** Накопичена зміна однієї комірки. */
export interface PendingEdit {
  rowKey: string;
  columnCode: string;
  /** Значення; `null` — стерти комірку (R-B4). */
  value: unknown;
  /** Явна порожнеча: користувач свідомо лишив комірку порожньою. */
  isEmpty: boolean;
  /** Версія рядка на момент читання; `null` — створення рядка (R-B2). */
  baseVersion: string | null;
}

/** Ключ комірки в накопичувачі. */
function keyOf(edit: Pick<PendingEdit, 'rowKey' | 'columnCode'>): string {
  return `${edit.rowKey}:${edit.columnCode}`;
}

/**
 * Збирає накопичені зміни в один запит.
 *
 * ⚠ Надсилаються **лише змінені** комірки. Повний зріз 500×60 на кожне
 * збереження — це тридцять тисяч комірок замість трьох, і саме він не
 * вкладається в бюджет 300 мс на сотню.
 *
 * ⚠ Три різні операції (R-B4) розрізняються явно: значення — записати,
 * `value: null` — стерти, `isEmpty: true` — явна порожнеча. Це різні наміри,
 * і злити їх в один означало б втратити відмінність між «тут нуль», «тут ще
 * не заповнювали» і «тут свідомо порожньо».
 */
export function buildRequest(
  tableInstanceId: number,
  periodKey: number,
  edits: readonly PendingEdit[],
  origin = 'UserEdit',
): PatchCellsRequest {
  const rows = new Map<string, { baseVersion: string | null; cells: PatchCell[] }>();

  for (const edit of edits) {
    const row = rows.get(edit.rowKey) ?? { baseVersion: edit.baseVersion, cells: [] };

    // ⚠ `value` присутнє ЗАВЖДИ, навіть при явній порожнечі. Контракт
    // розрізняє три наміри прапорцем `isEmpty`, а не наявністю поля: сама
    // комірка потрапляє в запит лише тоді, коли її змінюють, і «поля немає»
    // означало б «не чіпати» — тобто порожній намір у списку змін.
    row.cells.push({
      columnCode: edit.columnCode,
      value: edit.isEmpty ? null : edit.value,
      isEmpty: edit.isEmpty,
    });

    rows.set(edit.rowKey, row);
  }

  return {
    tableInstanceId,
    periodKey,
    origin,
    rows: [...rows].map(([rowKey, row]) => ({
      rowKey,
      baseVersion: row.baseVersion,
      cells: row.cells,
    })),
  };
}

/**
 * Надсилає пакет правок і повертає відповідь сервера.
 *
 * ⚠ Адреса несе ДОКУМЕНТ, а не лише екземпляр таблиці: маршрут контракту —
 * `PATCH /api/v1/documents/{documentId}/cells`. До аудиту клієнт бив у
 * `/api/v1/cells`, якого не існує, і збереження не працювало взагалі
 * (`A7-03`).
 *
 * ⛔ Окрема функція, а не тіло хука, саме тому, що надсилати доводиться й
 * ЗВІДКИ, де хука немає: зріз, чия сітка вже розмонтована перемиканням аркуша,
 * зберігає рівень документа (`autosave.ts`, `D14-12`). Доки цей `fetch` жив
 * усередині `useCellPatch`, «зберегти може лише змонтована сітка» було не
 * рішенням, а наслідком розташування коду.
 */
export function patchCells(
  documentId: number,
  request: PatchCellsRequest,
): Promise<PatchCellsResponse> {
  return apiFetch<PatchCellsResponse>(`/api/v1/documents/${documentId}/cells`, {
    method: 'PATCH',
    body: JSON.stringify(request),
  });
}

/**
 * Застосовує відповідь на патч до кешу зрізу — БЕЗ жодного запиту.
 *
 * ⛔ `CL-01`: тут стояв `invalidateQueries` зрізу — після КОЖНОГО успішного
 * збереження, тобто автозбереження коштувало `PATCH` плюс найважчий `GET`
 * системи. І мети він не досягав: перерахунок асинхронний, тож відповідь на
 * перезапит приходила здебільшого РАНІШЕ за нього, зі старими обчисленими
 * значеннями.
 *
 * ⚠ Тепер відповідь застосовується локально (`sliceApply.ts`): власні значення
 * оператора, нові `rowVersions`, округлення вставки — усе це вже є в запиті й
 * відповіді, і жодного запиту не потрібно. Обчислені колонки принесе
 * перерахунок; стежить за ним `useRecalculationStatus` нижче — по
 * `recalculationJobId` з відповіді (`BE-05`).
 *
 * ⛔ І стеження НЕ повертає сюди `invalidateQueries` зрізу. Опитування задачі
 * і перезапит зрізу — різні за ціною речі: перше коштує кілька сотень байтів
 * раз на дві секунди, друге — найважчий `GET` системи. Інвалідація на КОЖНЕ
 * опитування відкотила б `CL-01…03` рівно туди, звідки їх витягли.
 *
 * ⛔ Викликається і для зрізу, чия сітка вже розмонтована (`D14-12`): інакше
 * повернення на аркуш показувало б із кешу СТАРЕ значення — збережене на
 * сервері, але невидиме, тобто рівно той симптом, від якого лікує сховище.
 */
export function applyPatchLocally(
  queryClient: QueryClient,
  request: PatchCellsRequest,
  response: PatchCellsResponse,
): void {
  queryClient.setQueryData<TableSliceDto>(
    queryKeys.slices.one(request.tableInstanceId, request.periodKey),
    (slice) => (slice === undefined ? slice : applyPatchToSlice(slice, request, response)),
  );

  // ⚠ І зріз позначається застарілим — БЕЗ запиту (`refetchType: 'none'`).
  // Судження, яке варто назвати вголос: локальне застосування не знає
  // обчислених колонок, а `staleTime` зрізів — 5 хв (`CL-02`), тож без цього
  // рядка результат перерахунку не з'явився б до перезаходу. Так він
  // з'явиться при наступному монтуванні сітки (перемикання аркуша), не
  // коштуючи жодного запиту зараз.
  //
  // ⛔ Саме ПІСЛЯ `setQueryData`: успішний запис у кеш скидає позначку
  // `isInvalidated`, тож зворотний порядок нічого б не позначив.
  void queryClient.invalidateQueries({
    queryKey: queryKeys.slices.one(request.tableInstanceId, request.periodKey),
    refetchType: 'none',
  });
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

        // ⚠ `BE-05`: `null` у відповіді означає «перерахунку не поставлено»
        // (гілка відкладання `DAT-05`) — і тоді стеження ЗНІМАЄТЬСЯ, а не
        // лишається на попередній задачі: її результат уже не описує те, що
        // зараз у зрізі. `?? null` навмисно: поле необов'язкове в контракті,
        // тож `undefined` зі старішого сервера має читатися так само.
        setRecalculationJobId(response.recalculationJobId ?? null);

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
function moreConflictsOf(error: EcrApiError): number {
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
export interface RecalculationStatus {
  /** Стан задачі; `undefined` — відповіді ще немає. */
  state: string | undefined;

  /** Що показати оператору; `null` — стежити нема за чим. */
  outcome: JobOutcome | null;

  /**
   * Коли перерахунок завершився, `ГГ:ХХ`; `null` — ще йде або нема за чим
   * стежити.
   */
  finishedAt: string | null;
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
    refetchInterval: (query) =>
      pollInterval(query.state.data?.state) === false ? false : RecalculationPollMs,
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

/**
 * Надсилає останній пакет правок при закритті вкладки (`B-35`, `#38`).
 *
 * ⛔ Звичайний `apiFetch` тут не підходить: `beforeunload` не чекає на
 * `await`, сторінка вивантажується незалежно від того, дійшла відповідь чи
 * ні. `keepalive: true` — єдиний прапорець `fetch`, який браузер шанує саме
 * в цей момент: запит триває й після того, як документ зник, за умови, що
 * тіло вкладається в ліміт (≈64 КБ) — а пакет правок одного зрізу в нього
 * вкладається з великим запасом.
 *
 * ⚠ Відповідь навмисно ІГНОРУЄТЬСЯ: обробляти конфлікт чи оновлювати версії
 * рядків тут нема кому — вкладка вже зачиняється, а екран, який показав би
 * результат, зникає раніше за нього.
 *
 * ⛔ Відмова гаситься ЯВНО, і це не обробка помилки — це визнання, що
 * адресата в неї немає. Проковтнути тут чесно рівно тому, що інших
 * варіантів не існує: банер показати нема на чому (документ вивантажується в
 * цю саму мить), консоль зникає разом із вкладкою, а окремого логера чи
 * телеметрії, куди можна було б донести факт, у проєкті немає взагалі — і
 * заводити його тут, у гілці, яка виконується під час знищення сторінки,
 * означало б завести другий такий самий запит, який так само не встигне.
 * Альтернатива ж — лишити відхилення без обробника — не «нічого не робить»:
 * вона дає `unhandledrejection`, тобто шум, якого ніхто не читає, і ризик,
 * що обробник цієї події (свій чи чужий) зробить на вивантаженні щось іще.
 */
export function sendPatchBeacon(documentId: number, request: PatchCellsRequest): void {
  // ⚠ Два РІЗНІ шляхи відмови, і `try/catch` покриває лише перший:
  //   • синхронний кидок `fetch` (некоректний URL, заборона політикою) —
  //     ловиться `catch` нижче;
  //   • відхилення проміса (мережа впала, з'єднання обірване вивантаженням
  //     сторінки) — до `catch` не доходить ніколи, бо його вже немає в стеку.
  // Тому потрібні обидва запобіжники, а не один.
  const swallow = (): void => {
    // Навмисно порожньо — причина в коментарі до функції.
  };

  try {
    fetch(`/api/v1/documents/${documentId}/cells`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      keepalive: true,
      body: JSON.stringify(request),
    }).catch(swallow);
  } catch {
    swallow();
  }
}

/** Ключ комірки — експортується, щоб накопичувач і grid не розходилися. */
export { keyOf as cellEditKey };
