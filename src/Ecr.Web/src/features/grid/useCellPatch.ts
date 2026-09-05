import { useCallback, useRef, useState } from 'react';
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
 * Хук пакетного збереження комірок.
 *
 * ⚠ Дебаунс ~500 мс або `Ctrl+S`: зберігати на кожен натиск клавіші означало
 * б сотні запитів на один рядок, а не зберігати зовсім — втратити роботу при
 * закритті вкладки.
 */
export function useCellPatch(documentId: number): {
  patch: (request: PatchCellsRequest) => Promise<PatchCellsResponse>;
  /** Версії рядків після останнього успішного збереження. */
  rowVersions: Record<string, string>;
  isPending: boolean;
  conflicts: unknown[];
} {
  const queryClient = useQueryClient();
  const [isPending, setPending] = useState(false);
  const [conflicts, setConflicts] = useState<unknown[]>([]);
  const versions = useRef<Record<string, string>>({});

  const patch = useCallback(
    async (request: PatchCellsRequest): Promise<PatchCellsResponse> => {
      setPending(true);
      setConflicts([]);

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

        return response;
      } catch (error) {
        // ⛔ Конфлікт не «вирішується» мовчазним перезаписом: перелік
        // розбіжностей іде в діалог порівняння, і рішення ухвалює людина.
        if (error instanceof EcrApiError && error.isConflict) {
          setConflicts(error.conflicts);
        }

        void documentId;
        throw error;
      } finally {
        setPending(false);
      }
    },
    [documentId, queryClient],
  );

  return { patch, rowVersions: versions.current, isPending, conflicts };
}

/** Ключ комірки — експортується, щоб накопичувач і grid не розходилися. */
export { keyOf as cellEditKey };
