import { useCallback, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { PatchCell, PatchCellsRequest, PatchCellsResponse } from '@/api/types';

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
 * (`createDebouncer`, `registerUnloadFlush`), не цей хук: обидва механізми
 * потребують ще й `pending`-мапу, яку тримає `DocumentGrid`, тож самотужки
 * тут їх не зібрати. Хук натомість відповідає за ВИДИМИЙ підсумок: чи
 * зберігається зараз, чи збереглося, чи впало.
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
        // ⚠ Адреса несе ДОКУМЕНТ, а не лише екземпляр таблиці: маршрут
        // контракту — `PATCH /api/v1/documents/{documentId}/cells`. До аудиту
        // клієнт бив у `/api/v1/cells`, якого не існує, і збереження не
        // працювало взагалі (`A7-03`).
        const response = await apiFetch<PatchCellsResponse>(
          `/api/v1/documents/${documentId}/cells`,
          { method: 'PATCH', body: JSON.stringify(request) },
        );

        // ⚠ Версії оновлюються З ВІДПОВІДІ. Без цього наступний патч піде зі
        // старим `baseVersion` і отримає 409 на власних змінах — конфлікт із
        // самим собою, який неможливо пояснити користувачеві.
        versions.current = { ...versions.current, ...response.rowVersions };

        await queryClient.invalidateQueries({
          queryKey: ['table-slice', request.tableInstanceId, request.periodKey],
        });

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
 */
export function sendPatchBeacon(documentId: number, request: PatchCellsRequest): void {
  try {
    void fetch(`/api/v1/documents/${documentId}/cells`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      keepalive: true,
      body: JSON.stringify(request),
    });
  } catch {
    // ⚠ Найкраще, що можна зробити на вивантаженні сторінки, — спробувати:
    // показати помилку вже нема на чому, екран зникає в цю саму мить.
  }
}

/** Ключ комірки — експортується, щоб накопичувач і grid не розходилися. */
export { keyOf as cellEditKey };
