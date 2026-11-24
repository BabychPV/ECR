import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyRequiredInputsPanel } from '@/features/methodologies/MethodologyContentPanels';
import { testTheme } from '@/test/render';

/**
 * ✎ P-1124-1 (UI-прохід редактора скрипта 24.11): обов'язкову вхідну колонку можна не лише завести й змінити, а й
 * прибрати — після підтвердження. Скасування (кнопка, хрестик, Esc) запиту не шле.
 *
 * Мутація: прибрати `remove.mutate(removing)` у підтвердженні — перший тест червоний (DELETE не пішов).
 */

function mockApi(): { deletes: string[] } {
  let requiredInputs: unknown[] = [
    { id: 1, columnDefId: 5, columnCode: 'A', severity: 'Block', hintL10n: null, hasActiveBinding: true },
  ];
  const deletes: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs') && method === 'GET') return json(requiredInputs);
      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs/5') && method === 'DELETE') {
        deletes.push(url);
        requiredInputs = [];
        return new Response(null, { status: 204 });
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { deletes };
}

function show(editable = true): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MethodologyRequiredInputsPanel methodologyId={1} versionId={10} editable={editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyRequiredInputsPanel: прибрати вимогу', { timeout: 30_000 }, () => {
  it('«Remove» → підтвердження → DELETE …/required-inputs/5, рядок зникає', async () => {
    const api = mockApi();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.requiredInputRemove⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.requiredInputRemoveTitle⟧' });
    expect(api.deletes).toEqual([]);

    fireEvent.click(within(dialog).getByRole('button', { name: '⟦methodologies.requiredInputRemove⟧' }));

    await waitFor(() => {
      expect(api.deletes).toEqual(['/api/v1/methodologies/1/versions/10/required-inputs/5']);
    });
    expect(await screen.findByText('⟦methodologies.noRequiredInputs⟧')).toBeTruthy();
  });

  it('скасування в підтвердженні запиту не шле', async () => {
    const api = mockApi();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.requiredInputRemove⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦methodologies.requiredInputRemoveTitle⟧' });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(api.deletes).toEqual([]);
  });

  it('без права — кнопки «Remove» немає', async () => {
    mockApi();
    show(false);

    await screen.findByText('A');
    expect(screen.queryByRole('button', { name: '⟦methodologies.requiredInputRemove⟧' })).toBeNull();
  });
});
