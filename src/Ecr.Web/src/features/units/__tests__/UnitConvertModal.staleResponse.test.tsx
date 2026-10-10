import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import UnitConvertModal from '@/features/units/UnitConvertModal';
import type { UnitRef } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * N4-05: відповідь на застарілі поля не показується.
 *
 * Людина натиснула «Convert», поки запит летить змінила значення — відповідь на «1» не має стати «результатом»
 * для «2». Мутаційно: повернути `onSuccess: setResult` без ключа → тест червоний.
 */

const units = [
  { id: 1, code: 'kg', dimensionCode: 'mass', dimensionId: 1, factorToBase: '1', isBase: true },
  { id: 2, code: 'lb', dimensionCode: 'mass', dimensionId: 1, factorToBase: '0.4535923700', isBase: false },
] as unknown as UnitRef[];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitConvertModal: застаріла відповідь (N4-05)', () => {
  it('зміна значення, поки запит летить, ховає відповідь на попереднє значення', async () => {
    let release: (response: Response) => void = () => undefined;
    const pending = new Promise<Response>((resolve) => {
      release = resolve;
    });
    const fetchMock = vi.fn(() => pending);
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <UnitConvertModal units={units} initialFrom="kg" onClose={() => undefined} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByLabelText('⟦units.to⟧'));
    fireEvent.click(await screen.findByRole('option', { name: 'lb' }));

    fireEvent.click(screen.getByRole('button', { name: '⟦units.convert⟧' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    // Поле змінено, поки відповіді ще немає.
    fireEvent.change(screen.getByLabelText('⟦units.value⟧'), { target: { value: '2' } });

    release(
      new Response(JSON.stringify({ value: '2.2046226218', unit: 'lb' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    await waitFor(() => expect(screen.getByRole('button', { name: '⟦units.convert⟧' })).toBeDefined());
    // Дати відповіді осісти й переконатися, що вона НЕ показана.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByTestId('unit-convert-result')).toBeNull();
  });
});
