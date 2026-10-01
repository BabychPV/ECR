import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryImpactPage } from '../RegistryImpactPage';
import { testTheme } from '@/test/render';

/**
 * RT-25: перераховані документи зникають із переліку зачеплених, щойно задача перерахунку
 * завершилась, — без перезавантаження сторінки.
 *
 * ⚠ Сервер перестає повертати документ лише ПІСЛЯ перерахунку (прогін новіший за правку довідника),
 * тож інвалідація в момент постановки ще бачить старий перелік. Тест тримає цей порядок: перелік
 * порожніє лише тоді, коли задача вже `Succeeded`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubServer(): void {
  let jobReads = 0;
  let recalculated = false;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Registry.View', 'Calculation.Recalculate'], simulatedForUserId: null, userId: 9, userName: 'tester',
        });
      }
      if (path.endsWith('/impact')) {
        return json({
          items: recalculated
            ? []
            : [{ documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: ['methodology:HSE301'] }],
          total: recalculated ? 0 : 1,
          truncated: false,
        });
      }
      if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
        return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
      }
      if (path.includes('/api/v1/jobs/')) {
        jobReads += 1;
        // Перше читання — ще рахується; друге — готово, і лише тепер документи свіжі.
        const done = jobReads > 1;
        recalculated = done;
        return json({
          jobId: 'IRegistryImpactRecalculationJob#1',
          state: done ? 'Succeeded' : 'Running',
          percent: done ? 100 : 10,
          message: null,
          error: null,
        });
      }
      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage: перелік після завершення перерахунку', () => {
  /** Мутація: прибрати `onSettled` у `ImpactJob` — DOC5 лишається в переліку після `Succeeded`. */
  it('перераховані документи зникають із переліку, коли задача завершилась', async () => {
    stubServer();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

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

    // Постановка — ще не перерахунок: документ на місці, доки задача не завершилась.
    await screen.findByText(/registries\.impact\.jobQueued/);
    expect(screen.queryByRole('link', { name: 'DOC5' })).not.toBeNull();

    await waitFor(() => expect(screen.queryByRole('link', { name: 'DOC5' })).toBeNull(), { timeout: 5000 });
    expect(screen.getByText(/registries\.impact\.empty(?!Hint)/)).not.toBeNull();
  });
});
