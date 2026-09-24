import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { AuditPage } from '@/pages/admin/AuditPage';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { FilterBar } from '@/shared/ui/FilterBar';
import { testTheme } from '@/test/render';

/**
 * Поле, прив'язане до адреси, не губить жодної набраної клавіші, навіть коли
 * адреса запізнюється.
 *
 * ⛔ Живий стенд (2026-09-24, `drop-repro.mjs`, процесор сповільнено в 4×, по
 * 5 з 5): «Row key» на `/admin/audit?documentId=1` із набору `R12345` лишав
 * `"R"`/`"5"`, «Rule» на `/admin/consistency` із `CNS-0123` — `"C3"`, `"S3"`.
 * Поле було КЕРОВАНЕ значенням з адреси, а react-router застосовує навігацію
 * переходом, тобто пізніше за подію вводу: React повертав полю старе значення
 * між натисканнями, і наступна клавіша лягала поверх старого.
 *
 * Тут повільний рендер імітовано прямо: `setSearchParams` застосовується з
 * затримкою `LagMs`, тож кожна клавіша приходить, поки адреса ще несе старе
 * значення, а запізнілі відлуння проміжних значень прилітають ПОСЕРЕД набору.
 *
 * ⛔ Мутаційні докази (перевірено):
 *  1. повернути в полі `value={rowKey ?? ''}` / `value={rule}` / у
 *     `FilterBar.SearchField` `value={value ?? ''}` (поле знову кероване
 *     адресою) — червоніє відповідний випадок: у полі остання клавіша або
 *     запізніле проміжне значення;
 *  2. у `useFieldDraft.ts` замінити `if (!focused) setValue(external)` на
 *     `setValue(external)` — червоніють усі три випадки набору.
 */

const LagMs = 40;

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  const { useCallback } = await import('react');

  return {
    ...actual,
    useSearchParams: (...args: Parameters<typeof actual.useSearchParams>) => {
      const [params, setParams] = actual.useSearchParams(...args);
      const lagged = useCallback<typeof setParams>(
        (...call) => {
          setTimeout(() => setParams(...call), LagMs);
        },
        [setParams],
      );

      return [params, lagged] as const;
    },
  };
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function serveEmpty(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');
      if (url.pathname === '/api/v1/me') return json({ permissions: [], denies: [], grants: {} });

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );
}

let search = '';

function LocationProbe(): null {
  search = useLocation().search;

  return null;
}

function show(page: JSX.Element, path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>
          {page}
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Набір із паузою, меншою за запізнення адреси: відлуння приходять посеред набору. */
async function typeWhileUrlLags(input: HTMLElement, text: string): Promise<void> {
  const user = userEvent.setup({ delay: LagMs / 2 });
  await user.type(input, text);
}

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, LagMs * 4));

describe("поле, прив'язане до адреси, при запізнілій адресі", () => {
  it(
    '/admin/audit «Row key»: R12345 — у полі рівно R12345, адреса наздоганяє; «Reset» очищає поле',
    async () => {
      serveEmpty();
      show(<AuditPage />, '/admin/audit?documentId=1');

      const input = await screen.findByRole<HTMLInputElement>(
        'textbox',
        { name: '⟦audit.rowKey⟧' },
        { timeout: SlowEnvTimeout },
      );

      await typeWhileUrlLags(input, 'R12345');
      expect(input.value).toBe('R12345');

      await settle();
      expect(input.value).toBe('R12345');
      expect(new URLSearchParams(search).get('rowKey')).toBe('R12345');

      // Зовнішня зміна (кнопка переносить фокус) — поле її приймає.
      await userEvent.setup().click(screen.getByRole('button', { name: '⟦audit.reset⟧' }));
      await waitFor(() => expect(input.value).toBe(''));
      expect(new URLSearchParams(search).get('rowKey')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    '/admin/consistency «Rule»: CNS-0123 — у полі рівно CNS-0123',
    async () => {
      serveEmpty();
      show(<ConsistencyIssuesPage />, '/admin/consistency');

      const input = await screen.findByRole<HTMLInputElement>(
        'textbox',
        { name: '⟦consistency.rule⟧' },
        { timeout: SlowEnvTimeout },
      );

      await typeWhileUrlLags(input, 'CNS-0123');
      expect(input.value).toBe('CNS-0123');

      await settle();
      expect(input.value).toBe('CNS-0123');
      expect(new URLSearchParams(search).get('ruleCode')).toBe('CNS-0123');
    },
    SlowEnvTimeout,
  );

  it(
    'FilterBar (пошук): documents — у полі рівно набране; «Clear filters» очищає поле',
    async () => {
      show(<FilterBar search={{ label: 'Search' }} />, '/');

      const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Search' });

      await typeWhileUrlLags(input, 'documents');
      expect(input.value).toBe('documents');

      await settle();
      expect(input.value).toBe('documents');
      expect(new URLSearchParams(search).get('q')).toBe('documents');

      await userEvent.setup().click(screen.getByRole('button', { name: '×' }));
      await waitFor(() => expect(input.value).toBe(''));
      expect(new URLSearchParams(search).get('q')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
