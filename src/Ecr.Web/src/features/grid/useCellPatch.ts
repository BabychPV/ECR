import { useCallback, useEffect, useRef, useState } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  PatchCell,
  PatchCellsRequest,
  PatchCellsResponse,
  TableSliceDto,
} from '@/api/types';
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
 * перерахунок; стежити за ним клієнт зможе, коли `PatchCellsResponse` понесе
 * ідентифікатор задачі (контракт його не має — друга половина `CL-01`
 * заблокована серверною зміною).
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
  // коштуючи жодного запиту зараз. Повноцінне рішення — стеження за задачею —
  // чекає на ідентифікатор у `PatchCellsResponse`.
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
  conflicts: unknown[];
  /** Видимий індикатор для оператора — не лише лічильник незбереженого. */
  status: SaveStatus;
} {
  const queryClient = useQueryClient();
  const [isPending, setPending] = useState(false);
  const [conflicts, setConflicts] = useState<unknown[]>([]);
  const [status, setStatus] = useState<SaveStatus>('idle');
  const savedTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const versions = useRef<Record<string, string>>({});

  const patch = useCallback(
    async (request: PatchCellsRequest): Promise<PatchCellsResponse> => {
      setPending(true);
      setConflicts([]);
      setStatus('saving');
      if (savedTimer.current !== null) clearTimeout(savedTimer.current);

      try {
        const response = await patchCells(documentId, request);

        // ⚠ Версії оновлюються З ВІДПОВІДІ. Без цього наступний патч піде зі
        // старим `baseVersion` і отримає 409 на власних змінах — конфлікт із
        // самим собою, який неможливо пояснити користувачеві.
        versions.current = { ...versions.current, ...response.rowVersions };

        applyPatchLocally(queryClient, request, response);

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
          setConflicts(error.conflicts);
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

  return { patch, rowVersions: versions.current, isPending, conflicts, status };
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
