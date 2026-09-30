import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryImpactPage } from '@/features/registries/impact/RegistryImpactPage';
import { testTheme } from '@/test/render';

/**
 * «Вплив правки довідника» (RT-25): перелік, явна дія «Перерахувати» з причиною, стеження за
 * задачею, відмова 422, позначка `truncated`. Каталог не вантажиться — підписи це `⟦ключ⟧`.
 */

const Recalculate = '⟦registries.impact.recalculate⟧';

interface Scenario {
  permissions: string[];
  truncated?: boolean;
  enqueue?: { status: number; body: unknown };
}

function mockApi(scenario: Scenario): { posted: string[] } {
  const posted: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: {
            'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
          },
        });

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'admin',
          language: 'en',
          permissions: scenario.permissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.endsWith('/api/v1/registries/SUBST/impact')) {
        return json({
          items: [
            {
              documentId: 5,
              businessKey: 'DOC-5',
              periodKey: 202609,
              periodState: 'Open',
              via: ['methodology:M1'],
            },
          ],
          total: 1,
          truncated: scenario.truncated ?? false,
        });
      }

      if (url.endsWith('/api/v1/registries/SUBST/recalculate-impacted') && method === 'POST') {
        posted.push(typeof init?.body === 'string' ? init.body : '');
        const reply = scenario.enqueue ?? {
          status: 202,
          body: { jobId: 'J#1', statusUrl: '/api/v1/jobs/J%231' },
        };

        return json(reply.body, reply.status);
      }

      if (url.includes('/api/v1/jobs/')) {
        return json({
          jobId: 'J#1',
          state: 'Succeeded',
          percent: 100,
          message: null,
          error: null,
        });
      }

      throw new Error(`Немає заглушки для ${method} ${url}`);
    }),
  );

  return { posted };
}

function show(): void {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/registries/SUBST/impact']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage', () => {
  it('показує зачеплені документи; без права Calculation.Recalculate кнопки немає', async () => {
    mockApi({ permissions: ['Registry.View'] });
    show();

    await screen.findByText('DOC-5');
    expect(screen.getByText('methodology:M1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: Recalculate })).toBeNull();
  });

  it('truncated показує позначку, інакше її немає', async () => {
    mockApi({ permissions: [], truncated: true });
    show();
    await screen.findByText('DOC-5');
    expect(screen.getByText('⟦registries.impact.truncated⟧')).toBeTruthy();
  });

  it('«Перерахувати» з причиною ставить задачу й стежить до Succeeded', async () => {
    const api = mockApi({ permissions: ['Calculation.Recalculate'] });
    show();

    fireEvent.click(await screen.findByRole('button', { name: Recalculate }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), {
      target: { value: 'правка довідника' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: Recalculate }));

    await waitFor(() => expect(api.posted).toHaveLength(1));
    expect(JSON.parse(api.posted[0]!)).toEqual({
      documentIds: null,
      reason: 'правка довідника',
    });
    await waitFor(() => expect(document.querySelector('[data-outcome="succeeded"]')).not.toBeNull());
  });

  it('422 на постановці показується відмовою з текстом сервера, статусу задачі немає', async () => {
    mockApi({
      permissions: ['Calculation.Recalculate'],
      enqueue: {
        status: 422,
        body: {
          type: 'about:blank',
          title: 'Unprocessable',
          status: 422,
          detail: 'Причина обовʼязкова.',
          errorCode: 'ECR-REQ-0422',
          correlationId: 'c1',
          messageKey: 'err.ECR-REQ-0422.impactReasonRequired',
        },
      },
    });
    show();

    fireEvent.click(await screen.findByRole('button', { name: Recalculate }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), {
      target: { value: 'x' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: Recalculate }));

    await waitFor(() => expect(screen.getAllByRole('alert').length).toBeGreaterThan(0));
    expect(document.querySelector('[data-outcome]')).toBeNull();
  });
});
