import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import type { UnitRef } from '@/api/types';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import UnitConvertModal from '../UnitConvertModal';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Check a conversion» — один `POST /units/convert` на натиск (зонд: ЗАКРИТО, база 826f654e4).
 *
 * Запис тут не відбувається (лише розрахунок), власник (`UnitsPage`) монтує діалог лише за `{converting !== null && …}`.
 * Обробник уже звіряє `convert.isPending`, тож другий клік подвійного кліку нічого не шле; повторний натиск ПІСЛЯ
 * відповіді — законна перевірка ще раз, не дефект.
 *
 * Тест тримає це: без `if (convert.isPending) return;` — червоний.
 */

const Units = [
  { code: 't', dimensionId: 1 },
  { code: 'kg', dimensionId: 1 },
] as unknown as UnitRef[];

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: UnitConvertModal — один запит на натиск', () => {
  it('подвійний клік «Convert» шле один POST', async () => {
    const server = holdWrites({ write: () => ({ body: { value: '1000', unit: 'kg' } }) });
    renderKeepMounted(<UnitConvertModal units={Units} initialFrom="t" onClose={() => undefined} />);

    fireEvent.click(screen.getByRole('textbox', { name: /units\.to/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'kg', hidden: true }));
    const convert = screen.getByRole('button', { name: /units\.convert/ });
    await waitFor(() => {
      expect(convert).toHaveProperty('disabled', false);
    });

    fireEvent.click(convert);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/units/convert']);
    });
    await nextTick();
    fireEvent.click(convert);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/units/convert']);

    server.release();
    expect(await screen.findByTestId('unit-convert-result')).toBeDefined();
  });
});
