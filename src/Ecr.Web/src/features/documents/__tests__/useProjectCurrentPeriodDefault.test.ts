import { describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useProjectCurrentPeriodDefault } from '@/features/documents/useProjectCurrentPeriodDefault';

/**
 * T2-12: гонка з явним вибором періоду й зайві повтори.
 *
 * Запити на відкриття документа БЕЗ `?periodKey` (виміряно за кодом `DocumentPage`): до виправлення — один набір
 * (summary, tables, validation, tables/status…) за календарним місяцем; після — той самий набір за календарним
 * місяцем, поки календар проєкту (`GET /projects/{id}/periods`, він і раніше був) не відповів, і ще ОДИН набір за
 * поточним періодом проєкту (URL замінено один раз). Тобто +1 набір лише коли період у адресі відсутній; з
 * `?periodKey` — без змін (див. `DocumentPage.defaultPeriod.test`). Цей тест тримає межу «не більше одного запису».
 */
describe('useProjectCurrentPeriodDefault', () => {
  const periods = [{ periodKey: 202609, isCurrent: true }];

  it('пише поточний період рівно один раз, навіть коли календар перезавантажується', () => {
    const setPeriod = vi.fn();
    const { rerender } = renderHook(
      ({ url, list }: { url: number | null; list: typeof periods | undefined }) =>
        useProjectCurrentPeriodDefault(url, list, setPeriod),
      { initialProps: { url: null as number | null, list: undefined as typeof periods | undefined } },
    );

    expect(setPeriod).not.toHaveBeenCalled();

    rerender({ url: null, list: periods });
    rerender({ url: null, list: [...periods] });

    expect(setPeriod).toHaveBeenCalledTimes(1);
    expect(setPeriod).toHaveBeenCalledWith(202609);
  });

  it('явний вибір до відповіді календаря не перебивається (гонки немає)', () => {
    const setPeriod = vi.fn();
    const { rerender } = renderHook(
      ({ url, list }: { url: number | null; list: typeof periods | undefined }) =>
        useProjectCurrentPeriodDefault(url, list, setPeriod),
      { initialProps: { url: null as number | null, list: undefined as typeof periods | undefined } },
    );

    rerender({ url: 202501, list: undefined });
    rerender({ url: 202501, list: periods });

    expect(setPeriod).not.toHaveBeenCalled();
  });

  it('після відповіді людина може змінити період — хук не повертає поточний', () => {
    const setPeriod = vi.fn();
    const { rerender } = renderHook(
      ({ url, list }: { url: number | null; list: typeof periods }) =>
        useProjectCurrentPeriodDefault(url, list, setPeriod),
      { initialProps: { url: null as number | null, list: periods } },
    );

    expect(setPeriod).toHaveBeenCalledTimes(1);

    rerender({ url: 202608, list: periods });
    rerender({ url: null, list: periods });

    expect(setPeriod).toHaveBeenCalledTimes(1);
  });
});
