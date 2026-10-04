import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { impactJobRefetchInterval, RegistryImpactPage } from '../RegistryImpactPage';
import { PollMs } from '@/features/workflow/jobFollow';
import type { JobStatus } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * L9-41 (AUDIT-2026-10-03, 1E): відмова читання задачі перерахунку зупиняє опитування.
 *
 * До фіксу `403` на `GET /jobs/{id}` (немає права бачити задачу) лишав `data` порожнім, а порожнє
 * `data` означало «опитувати далі» — запит ішов кожні `PollMs` доки відкрита сторінка.
 *
 * Мутаційний доказ (перевірено руками 2026-10-04): `refetchInterval: (q) => impactPollInterval(q.state.data)`
 * (як до фіксу) → червоні обидва тести.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage: опитування задачі після відмови (L9-41)', () => {
  it('інтервал: відмова — стоп; ще не прочитано — опитувати; стан задачі — як раніше', () => {
    const running = { jobId: 'x', state: 'Running', percent: 1, message: null, error: null } as unknown as JobStatus;

    expect(impactJobRefetchInterval({ status: 'error', data: undefined })).toBe(false);
    expect(impactJobRefetchInterval({ status: 'pending', data: undefined })).toBe(PollMs);
    expect(impactJobRefetchInterval({ status: 'success', data: running })).toBe(PollMs);
  });

  it('403 на читання задачі — одне читання, а не безкінечне опитування', async () => {
    let jobReads = 0;
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
            items: [{ documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: [] }],
            total: 1,
            truncated: false,
          });
        }
        if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
          return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
        }
        if (path.includes('/api/v1/jobs/')) {
          jobReads += 1;
          return json({ title: 'Forbidden', status: 403 }, 403);
        }
        return json(null);
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
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
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'причина' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /registries\.impact\.recalculateConfirm/ }));

    await waitFor(() => expect(document.querySelector('[data-impact-job-state="unknown"]')).not.toBeNull());

    // Два інтервали опитування — до фіксу за цей час ішло ще два читання.
    await new Promise((resolve) => setTimeout(resolve, PollMs * 2 + 300));
    expect(jobReads).toBe(1);
  }, 15_000);
});
