import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { buildRequest, conflictTimeLabel, useCellPatch } from '../useCellPatch';
import { formatTime } from '@/shared/format';
import type { CellConflictDto } from '@/api/types';

/**
 * `BE-06`: конфлікт доїжджає до інтерфейсу з чиїм значенням, ім'ям автора й
 * моментом правки — і з чесним `null` там, де сервер нічого не знає.
 *
 * ⛔ До цієї роботи хук віддавав `unknown[]`, а сервер — заглушки (`null`,
 * `""`, поточний час сервера). Тобто показати було нічого і нічим: сітка мала
 * лише лічильник «змінено комірок: N», який не веде до жодної дії.
 */

const conflict: CellConflictDto = {
  rowKey: 'R1',
  columnCode: 'C2',
  yourValue: 9,
  theirValue: 12.4,
  theirUser: 'A. Serikbayev',
  theirOrigin: 'UserEdit',
  theirChangedAt: '2026-02-01T09:15:00.0000000Z',
  currentVersion: '0xFF',
};

function respondWithConflict(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    ),
  );
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useCellPatch — подробиці конфлікту', () => {
  it('чиє значення, ім’я автора й момент правки доїжджають у стан хука', async () => {
    respondWithConflict({
      title: 'Конфлікт',
      status: 409,
      errorCode: 'ECR-CELL-0409',
      correlationId: 'c1',
      conflicts: [conflict],
      moreConflicts: '0',
    });

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    await act(async () => {
      await expect(result.current.patch(buildRequest(700, 202602, []))).rejects.toThrow();
    });

    const [shown] = result.current.conflicts;

    expect(shown?.theirValue).toBe(12.4);

    // ⛔ ІМ'Я, а не логін і не SID (`R-A2`, `D-86`).
    expect(shown?.theirUser).toBe('A. Serikbayev');
    expect(shown?.theirChangedAt).toBe('2026-02-01T09:15:00.0000000Z');
    expect(result.current.moreConflicts).toBe(0);
  });

  it('обрізаний перелік несе лічильник решти — числом, хоч приходить рядком', async () => {
    respondWithConflict({
      title: 'Конфлікт',
      status: 409,
      errorCode: 'ECR-CELL-0409',
      correlationId: 'c1',
      conflicts: [conflict],

      // ⚠ Сервер кладе числа в розширення РЯДКАМИ — така конвенція
      // `ProblemDetails` там (у шаблон каталогу підставляються лише `string`).
      moreConflicts: '37',
    });

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    await act(async () => {
      await expect(result.current.patch(buildRequest(700, 202602, []))).rejects.toThrow();
    });

    expect(result.current.moreConflicts).toBe(37);
  });

  it('сервер без лічильника (або з дурницею замість числа) дає 0, а не NaN', async () => {
    respondWithConflict({
      title: 'Конфлікт',
      status: 409,
      errorCode: 'ECR-CELL-0409',
      correlationId: 'c1',
      conflicts: [conflict],
      moreConflicts: 'багато',
    });

    const { result } = renderHook(() => useCellPatch(7), { wrapper });

    await act(async () => {
      await expect(result.current.patch(buildRequest(700, 202602, []))).rejects.toThrow();
    });

    // ⛔ `NaN` у реченні «і ще N комірок» гірший за мовчання.
    expect(result.current.moreConflicts).toBe(0);
    expect(Number.isNaN(result.current.moreConflicts)).toBe(false);
  });
});

describe('conflictTimeLabel', () => {
  it('момент правки пишеться тим самим часом, що й решта екрана', () => {
    const at = new Date('2026-02-01T09:15:00Z');

    /*
     * ✎ 2026-09-19. Очікуване значення складалося В ТЕСТІ тим самим способом,
     * яким його складав продукт (`ГГ:ХХ` вручну). Такий тест дзеркалить
     * реалізацію й тому не може побачити, що вона розходиться з рештою
     * застосунку: під `en` кожна інша позначка часу — `2:05 PM`
     * (`formatTime`), а ця була `14:05`.
     */
    expect(conflictTimeLabel('2026-02-01T09:15:00Z')).toBe(formatTime(at));
  });

  it('перевірка вище не осліпне, якщо мову набору змінять', () => {
    /*
     * ⛔ Передумова попереднього тесту, названа вголос. Рівність із
     * `formatTime` ловить повернення власного `ГГ:ХХ` лише доти, доки мова
     * набору дає ЗАПИС, ВІДМІННИЙ від двоцифрового 24-годинного (під `en` це
     * `9:15 AM`). Якщо дефолт набору колись стане `ru` чи `kz`, обидва записи
     * збіжаться, і той тест мовчки перестане щось доводити — тому умова
     * перевіряється окремо й падає з поясненням, а не тихне.
     */
    const handRolled = (at: Date): string =>
      `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;

    const at = new Date('2026-02-01T09:15:00Z');

    expect(
      formatTime(at),
      'мова набору дає той самий запис, що й ручний ГГ:ХХ — рівність вище стала порожньою',
    ).not.toBe(handRolled(at));
  });

  it('невідомий момент лишається невідомим, а не стає «зараз»', () => {
    // ⛔ ГОЛОВНЕ твердження. Мутація «повернути поточний час замість null»
    // валить рівно цей рядок — і вона ж була дефектом на сервері: діалог
    // показував би «їхня правка 14:02» на правці, якої ніхто не знає.
    expect(conflictTimeLabel(null)).toBeNull();
    expect(conflictTimeLabel('не дата')).toBeNull();
  });
});
