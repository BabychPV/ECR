import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryImpactPage } from '../RegistryImpactPage';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';
import { testTheme } from '@/test/render';

/**
 * RT-25: після «Перерахувати зачеплені» кеш переліку зачеплених і панелі результатів методологій
 * інвалідується — інакше обидва показують стан ДО постановки перерахунку.
 *
 * ⚠ Ключі беруться з коду, що їх читає (`calculationResultsKey`), а не переписуються літералом:
 * розбіжні ключі й були б тим дефектом, який тест має ловити.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = (String(input).split('?')[0] ?? '');
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Registry.View', 'Calculation.Recalculate'], simulatedForUserId: null, userId: 9, userName: 'tester',
        });
      }
      if (path.endsWith('/impact')) {
        return json({
          items: [{ documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: ['methodology:HSE301'] }],
          total: 1,
          truncated: false,
        });
      }
      if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
        return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
      }
      if (path.includes('/api/v1/jobs/')) {
        return json({ jobId: 'IRegistryImpactRecalculationJob#1', state: 'Running', percent: 10, message: 'reading', error: null });
      }
      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage: інвалідація кешу після перерахунку', () => {
  it('інвалідує ключ сторінки впливу і запити результатів методологій', async () => {
    stubServer();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const resultsKey = calculationResultsKey(5, 202609);
    client.setQueryData(resultsKey, []);
    client.setQueryData(['document', 5, 202609, 'other'], []);
    const spy = vi.spyOn(client, 'invalidateQueries');

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/registries/COMPONENT/impact']}>
            <Routes>
              <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByRole('link', { name: 'DOC5' });
    fireEvent.click(await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'склад газу оновлено' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /registries\.impact\.recalculateConfirm/ }));

    await waitFor(() => expect(spy).toHaveBeenCalledTimes(2));
    expect(spy.mock.calls.some(([filters]) => JSON.stringify(filters?.queryKey) === JSON.stringify(['registry-impact', 'COMPONENT']))).toBe(true);

    // Запит результатів інвалідовано, а сторонній запит того ж документа — ні.
    expect(client.getQueryState(resultsKey)?.isInvalidated).toBe(true);
    expect(client.getQueryState(['document', 5, 202609, 'other'])?.isInvalidated).toBe(false);
  });
});
