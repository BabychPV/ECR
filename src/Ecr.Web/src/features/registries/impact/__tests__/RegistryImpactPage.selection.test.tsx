import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryImpactPage } from '../RegistryImpactPage';
import { testTheme } from '@/test/render';

/**
 * L9-35 (AUDIT-2026-10-03, 1E): обраний документ, що зник із переліку, не йде в запит перерахунку.
 *
 * До фіксу вибір не скидався після постановки, і наступне «Перерахувати» слало id документа, якого сервер
 * уже не повертає (`422`); так само після чужої правки, що прибрала документ із переліку.
 *
 * Мутаційні докази (перевірено руками 2026-10-04): прибрати `setSelected(new Set())` в `onSuccess` →
 * червоний «після постановки»; рахувати тіло й підпис за `selected` замість `effective` → червоний
 * «зник із переліку».
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Doc5 = { documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: ['methodology:HSE301'] };
const Doc7 = { documentId: 7, businessKey: 'DOC7', periodKey: 202608, periodState: 'Grace', via: ['methodology:HSE301'] };

function mockServer(impact: () => unknown): { readonly posts: unknown[] } {
  const posts: unknown[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/impact')) return json(impact());

      if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
        posts.push(JSON.parse(String(init.body)));
        return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
      }

      if (path.includes('/api/v1/jobs/')) {
        return json({ jobId: 'IRegistryImpactRecalculationJob#1', state: 'Running', percent: 10, message: null, error: null });
      }

      return json(null);
    }),
  );

  return { posts };
}

function show(): QueryClient {
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

  return client;
}

async function recalculateWith(reason: string): Promise<void> {
  fireEvent.click(document.querySelector<HTMLButtonElement>('[data-impact-recalculate]') as HTMLButtonElement);
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: reason } });
  fireEvent.click(within(dialog).getByRole('button', { name: /registries\.impact\.recalculateConfirm/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage: вибір документів не переживає перелік (L9-35)', () => {
  it('після постановки вибір скинуто — галочка знята, підпис і наступний запит «усі»', async () => {
    // ⚠ Поки задача рахується, сервер ще повертає той самий перелік: документ на місці, а вибір — ні.
    const server = mockServer(() => ({ items: [Doc5, Doc7], total: 2, truncated: false }));
    show();

    const doc7 = await screen.findByRole('checkbox', { name: /DOC7/ });
    fireEvent.click(doc7);
    await recalculateWith('перша');
    await waitFor(() => expect(server.posts).toHaveLength(1));
    expect(server.posts[0]).toEqual({ documentIds: [7], reason: 'перша' });

    await waitFor(() => expect((screen.getByRole('checkbox', { name: /DOC7/ }) as HTMLInputElement).checked).toBe(false));
    const button = document.querySelector<HTMLButtonElement>('[data-impact-recalculate]') as HTMLButtonElement;
    expect(button.textContent).toContain('registries.impact.recalculateAll');

    await recalculateWith('друга');
    await waitFor(() => expect(server.posts).toHaveLength(2));
    expect(server.posts[1]).toEqual({ documentIds: null, reason: 'друга' });
  });

  it('обраний документ зник із переліку з іншої причини — у запит і підпис іде лише наявний', async () => {
    let items = [Doc5, Doc7];
    const server = mockServer(() => ({ items, total: items.length, truncated: false }));
    const client = show();

    fireEvent.click(await screen.findByRole('checkbox', { name: /DOC5/ }));
    fireEvent.click(screen.getByRole('checkbox', { name: /DOC7/ }));

    const button = document.querySelector<HTMLButtonElement>('[data-impact-recalculate]') as HTMLButtonElement;
    expect(button.textContent).toContain('count=2');

    // Чужий перерахунок прибрав DOC7.
    items = [Doc5];
    await client.invalidateQueries({ queryKey: ['registry-impact', 'COMPONENT'] });
    await waitFor(() => expect(document.querySelector('[data-impact-row="7"]')).toBeNull());

    expect(button.textContent).toContain('count=1');

    await recalculateWith('лише наявні');
    await waitFor(() => expect(server.posts).toHaveLength(1));
    expect(server.posts[0]).toEqual({ documentIds: [5], reason: 'лише наявні' });
  });
});
