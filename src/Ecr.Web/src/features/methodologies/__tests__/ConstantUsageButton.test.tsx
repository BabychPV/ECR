import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';

/**
 * Афорданс «де використано» на рядку константи в `MethodologyConstantsPanel`
 * (ФВ-8.14) — картка константи у версії методики.
 *
 * ⛔ Мутаційний доказ: кнопка має бути на рядку ЗАВЖДИ, незалежно від
 * `editable` (право редагування — окреме від права переглянути використання,
 * `Calculation.View`) і незалежно від того, чи є у константи використання
 * (`total: 0` — теж чинна відповідь, не привід ховати афорданс).
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const Constant = {
  id: 5,
  code: 'K1',
  kind: 'Numeric' as const,
  value: '1.5',
  textValue: null,
  unitId: null,
  validFrom: null,
  validTo: null,
  category: null,
  source: null,
  isResolved: true,
};

function mockApi(usageTotal: number): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/constants/K1/usage')) {
        return json({
          total: usageTotal,
          items: usageTotal === 0 ? [] : [{ kind: 'templateFormula', id: '9', label: 'F', route: null }],
        });
      }

      if (url.includes('/api/v1/units')) {
        return json([]);
      }

      if (url.includes('/api/v1/methodologies/1/versions/2/constants')) {
        return json([Constant]);
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

async function show(editable: boolean): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const panels = await import('../MethodologyContentPanels');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <panels.MethodologyConstantsPanel methodologyId={1} versionId={2} editable={editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyConstantsPanel: афорданс «де використано»', () => {
  it.each([true, false])('кнопка на рядку константи є при editable=%s', async (editable) => {
    mockApi(0);
    await show(editable);

    expect(await screen.findByRole('button', { name: /registries\.tabUsage/ })).toBeDefined();
  });

  it('total: 0 — модалка все одно показує «ніде не використано», не порожньо', async () => {
    mockApi(0);
    await show(true);

    fireEvent.click(await screen.findByRole('button', { name: /registries\.tabUsage/ }));

    const dialog = await screen.findByRole('dialog');

    await waitFor(() => {
      expect(within(dialog).getByText(/registries\.usageNone/)).toBeDefined();
    });
  });

  it('успіх із записом — перелік у модалці', async () => {
    mockApi(1);
    await show(true);

    fireEvent.click(await screen.findByRole('button', { name: /registries\.tabUsage/ }));

    const dialog = await screen.findByRole('dialog');

    await waitFor(() => {
      expect(within(dialog).getByText('F')).toBeDefined();
    });
  });
});
