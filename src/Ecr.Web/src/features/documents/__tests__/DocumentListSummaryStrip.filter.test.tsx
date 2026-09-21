import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentListSummaryStrip } from '@/features/documents/DocumentListSummaryStrip';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Клікабельні лічильники смуги над переліком (`BE-09b`).
 *
 * ⚠ Перевіряється АДРЕСА ЗАПИТУ переліку, а не лише `aria-pressed`: кнопка,
 * яка підсвічується, але не доносить `state` до сервера, виглядає робочою і
 * мовчки показує весь перелік.
 *
 * ⚠ Рядки — ключами в `⟦…⟧`: каталогу в цьому тесті немає.
 */

const base = { createdAt: '2026-01-01T00:00:00Z', projectId: 1, sheetCount: 1, sheetStates: {} };
const Row = { ...base, id: 1, businessKey: 'DOC-0001', hasLateEdits: false };

const Summary = { draft: 7, submitted: 3, approved: 12, rejected: 0, withIssues: 4 };

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

/** Адреси запитів переліку (без `/summary`). */
const listed: string[] = [];
const summaries: string[] = [];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/documents/summary')) {
        summaries.push(url);
        return json(Summary);
      }

      if (url.includes('/api/v1/documents')) {
        listed.push(url);
        return json({ items: [Row], nextCursor: null, totalCount: 1 });
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      return json(null);
    }),
  );
}

let search = '';
function LocationSpy(): null {
  search = useLocation().search;
  return null;
}

function show(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <LocationSpy />
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const lastListed = (): URLSearchParams => new URLSearchParams(listed.at(-1)?.split('?')[1] ?? '');
const urlState = (): string | null => new URLSearchParams(search).get('state');

afterEach(() => {
  vi.unstubAllGlobals();
  listed.length = 0;
  summaries.length = 0;
  search = '';
});

const Slow = 120_000;
const Find = 45_000;

const strip = (): Promise<HTMLElement> =>
  screen.findByRole('group', { name: '⟦documents.summaryLabel⟧' }, { timeout: Find });

const counter = async (state: 'Draft' | 'Submitted' | 'Approved' | 'Rejected'): Promise<HTMLElement> =>
  within(await strip()).getByRole('button', { name: new RegExp(`status\\.sheet\\.${state}`) });

describe('DocumentListSummaryStrip: лічильник — фільтр стану (BE-09b)', () => {
  it(
    'клік на лічильник ставить state в адресу і в запит, позначає його aria-pressed і лишає на ньому фокус',
    async () => {
      mockFetch();
      show('/?periodKey=202601&cursor=abc');

      const draft = await counter('Draft');
      expect(draft.tagName).toBe('BUTTON');
      expect(draft.getAttribute('aria-pressed')).toBe('false');

      draft.focus();
      fireEvent.click(draft);

      await waitFor(() => expect(lastListed().get('state')).toBe('Draft'), { timeout: Find });
      expect(lastListed().get('periodKey')).toBe('202601');
      expect(lastListed().has('cursor')).toBe(false);
      expect(urlState()).toBe('Draft');

      // Той самий вузол: смуга не перемонтовується на час запиту.
      const pressed = await counter('Draft');
      expect(pressed).toBe(draft);
      expect(pressed.getAttribute('aria-pressed')).toBe('true');
      expect(pressed.getAttribute('data-summary-active')).toBe('true');
      expect(document.activeElement).toBe(pressed);

      // Одне джерело правди: фільтр над переліком показує те саме.
      const field = screen.getByRole('combobox', { name: '⟦documents.state⟧' }) as HTMLSelectElement;
      expect(field.value).toBe('Draft');
    },
    Slow,
  );

  it(
    'повторний клік на активний лічильник знімає фільтр — з адреси і з запиту',
    async () => {
      mockFetch();
      show('/?periodKey=202601&state=Submitted');

      const submitted = await counter('Submitted');
      expect(submitted.getAttribute('aria-pressed')).toBe('true');
      await waitFor(() => expect(lastListed().get('state')).toBe('Submitted'), { timeout: Find });

      fireEvent.click(submitted);

      await waitFor(() => expect(urlState()).toBeNull(), { timeout: Find });
      await waitFor(() => expect(lastListed().has('state')).toBe(false), { timeout: Find });
      expect(new URLSearchParams(search).get('periodKey')).toBe('202601');
      expect((await counter('Submitted')).getAttribute('aria-pressed')).toBe('false');
    },
    Slow,
  );

  it(
    'клік на інший лічильник перемикає стан, активний завжди один',
    async () => {
      mockFetch();
      show('/?periodKey=202601&state=Draft');

      fireEvent.click(await counter('Approved'));

      await waitFor(() => expect(urlState()).toBe('Approved'), { timeout: Find });
      const buttons = within(await strip()).getAllByRole('button');
      expect(buttons.filter((button) => button.getAttribute('aria-pressed') === 'true')).toEqual([
        await counter('Approved'),
      ]);
    },
    Slow,
  );

  it(
    'вибір у фільтрі позначає відповідний лічильник — смуга читає ту саму адресу',
    async () => {
      mockFetch();
      show('/?periodKey=202601');

      await counter('Draft');
      const field = await screen.findByRole('combobox', { name: '⟦documents.state⟧' }, { timeout: Find });
      fireEvent.change(field, { target: { value: 'Approved' } });

      await waitFor(async () => expect((await counter('Approved')).getAttribute('aria-pressed')).toBe('true'), {
        timeout: Find,
      });
      expect((await counter('Draft')).getAttribute('aria-pressed')).toBe('false');
    },
    Slow,
  );

  it(
    '«з проблемами» — не стан, тож не кнопка; кнопок рівно стільки, скільки лічильників стану',
    async () => {
      mockFetch();
      show('/?periodKey=202601');

      const group = await strip();

      expect(within(group).getAllByRole('button')).toHaveLength(3);
      const issues = group.querySelector('[data-summary-counter="withIssues"]');
      expect(issues?.tagName).toBe('DIV');
      expect(issues?.hasAttribute('aria-pressed')).toBe(false);
    },
    Slow,
  );

  it(
    'активний фільтр Rejected лишає лічильник на смузі і при нулі — без кольору, щоб його було чим зняти',
    async () => {
      mockFetch();
      show('/?periodKey=202601&state=Rejected');

      const rejected = await counter('Rejected');

      expect(rejected.getAttribute('aria-pressed')).toBe('true');
      expect(rejected.textContent).toContain('0');
      expect(rejected.hasAttribute('data-summary-tone')).toBe(false);
    },
    Slow,
  );

  it(
    'без періоду лічильників-кнопок немає: зведення не запитується, state у запит не йде',
    async () => {
      mockFetch();
      show('/?state=Draft');

      await screen.findByRole('table', {}, { timeout: Find });

      expect(screen.queryByRole('group', { name: '⟦documents.summaryLabel⟧' })).toBeNull();
      expect(document.querySelector('[data-summary-counter]')).toBeNull();
      expect(summaries).toHaveLength(0);
      expect(listed.every((url) => !new URLSearchParams(url.split('?')[1] ?? '').has('state'))).toBe(true);
    },
    Slow,
  );

  it(
    'без періоду смуга не малює кнопок, навіть коли зведення вже лежить у кеші',
    () => {
      /*
       * ⚠ Сторожить явну перевірку `periodKey` у самій смузі, а не лише
       * `enabled` запиту: дані під ключем без періоду можуть з'явитися повз
       * запит (кеш, префетч), і тоді кнопка ставила б `state`, на який сервер
       * відповідає `422`.
       */
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      client.setQueryData(['documents', 'summary', null], Summary);
      const setState = vi.fn();

      render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={client}>
            <DocumentListSummaryStrip periodKey={null} filters={{ state: null, setState }} />
          </QueryClientProvider>
        </MantineProvider>,
      );

      expect(screen.queryAllByRole('button')).toHaveLength(0);
      expect(document.querySelector('[data-summary-counter]')).toBeNull();
    },
  );
});
