import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';

/**
 * `X-29`: у вікні політик періодів (1) колонка дій мала порожню клітинку
 * заголовка — читалка оголошувала кнопку «Save» без назви колонки; (2) кнопка
 * внизу звалася «Cancel», хоча рядки вище зберігаються кожен своєю кнопкою
 * одразу, — тобто обіцяла скасувати вже записане, а лише закривала вікно.
 */

const Policies = [
  { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodPolicyManager: чесні підписи', () => {
  it('колонка дій названа, а вікно закривається «Close», не «Cancel»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(JSON.stringify(Policies), { status: 200, headers: { 'Content-Type': 'application/json' } }),
        ),
      ),
    );

    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodPolicyManager />
        </QueryClientProvider>
      </MantineProvider>,
    );

    screen.getByRole('button', { name: /periods\.managePolicies/ }).click();

    // ⛔ Мутація «повернути порожній `<Table.Th />`» — назви немає.
    expect(await screen.findByRole('columnheader', { name: '⟦common.actions⟧' })).toBeTruthy();

    // ⛔ Мутація «повернути `common.cancel`» — кнопка знову «Cancel».
    expect(screen.getByRole('button', { name: '⟦common.close⟧' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '⟦common.cancel⟧' })).toBeNull();
  });
});
