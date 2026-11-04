import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Колонка «Version» переліку документів: версія шаблону за обраний період
 * (`templateVersionByPeriod[період] ?? templateVersion`); сортування й фільтр - СЕРВЕРНІ (`sort`, `templateVersions`),
 * в адресі `vsort`/`versions`. Клієнт порядку не міняє: показує сторінку так, як її віддав сервер.
 */
const SlowEnvTimeout = 400_000;

const item = (id: number, templateVersion: string | null, templateVersionByPeriod: Record<string, string> | null) => ({
  id,
  businessKey: `DOC-00000${id}`,
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
  hasLateEdits: false,
  templateVersion,
  templateVersionByPeriod,
});

// Порядок сервера: 1 (2.0), 2 (1.0 → 3.0 за 202609), 3 (без версії), 4 (1.0)
const Items = [item(1, '2.0', null), item(2, '1.0', { '202609': '3.0' }), item(3, null, null), item(4, '1.0', null)];

/** Запити переліку документів у порядку виклику (повні адреси). */
let listed: string[] = [];

/** Запити варіантів фільтра «Version» (`GET /documents/template-versions`) і їхня відповідь (статус 200, якщо не `null`). */
let optionRequests: string[] = [];
let optionsAnswer: { items: { version: string; count: number }[]; withoutVersionCount: number } | null = {
  items: [
    { version: '1.0', count: 2 },
    { version: '2.0', count: 1 },
    { version: '2.10', count: 5 },
    { version: '3.0', count: 1 },
  ],
  withoutVersionCount: 1,
};

/**
 * `answer` - що віддає сервер на запит переліку (за його адресою); типово - усі рядки в порядку сервера. ⛔ Порядок і
 * склад відповіді задає ТІЛЬКИ вона: тести доводять, що клієнт нічого не пересортовує й не відфільтровує сам.
 */
function mockFetch(answer: (params: URLSearchParams) => typeof Items = () => Items): void {
  listed = [];
  optionRequests = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 4, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents/template-versions')) {
        optionRequests.push(url);
        return optionsAnswer === null ? new Response('{}', { status: 500 }) : json(optionsAnswer);
      }
      if (url.includes('/api/v1/documents')) {
        listed.push(url);
        const items = answer(new URL(url, 'http://localhost').searchParams);
        return json({ items, nextCursor: null, totalCount: items.length });
      }
      if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

const lastListed = (): URLSearchParams => new URL(listed[listed.length - 1] ?? '/', 'http://localhost').searchParams;

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.search}</span>;
}

function show(url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const rowKeys = (): string[] =>
  Array.from(document.querySelectorAll('[data-document-row]')).map((row) => row.getAttribute('data-document-row') ?? '');

const versionOf = (key: string): string | null =>
  document.querySelector(`[data-document-row="${key}"] [data-document-version]`)?.getAttribute('data-document-version') ?? null;

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Відкриває мультивибір «Version» і віддає підписи його варіантів (у порядку списку). */
async function versionOptionLabels(): Promise<string[]> {
  const user = userEvent.setup();
  const field = await waitFor(
    () => {
      const found = document.querySelector<HTMLElement>('[data-version-filter]');
      expect(found).not.toBeNull();
      return found!;
    },
    { timeout: SlowEnvTimeout },
  );
  await user.click(field);

  // На сторінці є й інші списки (проєкти, власники): беремо лише варіанти версій.
  await screen.findAllByRole('option', {}, { timeout: SlowEnvTimeout });

  return screen
    .getAllByRole('option')
    .map((option) => option.textContent ?? '')
    .filter((label) => label.includes('documents.versionValue'));
}

describe('DocumentsPage: колонка «Version»', () => {
  it(
    'показує версію ПЕРІОДУ (карта перекриває типову), а без версії — «—»',
    async () => {
      mockFetch();
      show('/?periodKey=202609');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(versionOf('DOC-000001')).toBe('2.0');
      expect(versionOf('DOC-000002')).toBe('3.0');
      expect(versionOf('DOC-000003')).toBe('none');
      // Підказка «за період» - лише там, де значення з карти.
      const fromMap = document.querySelector('[data-document-row="DOC-000002"] [data-document-version]');
      expect(fromMap?.getAttribute('data-version-source')).toBe('period');
      expect(fromMap?.getAttribute('title')).toBe('⟦documents.versionForPeriod (period=September 2026)⟧');
      expect(document.querySelector('[data-document-row="DOC-000001"] [data-document-version]')?.getAttribute('title')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'в іншому періоді та сама картка показує типову версію',
    async () => {
      mockFetch();
      show('/?periodKey=202610');

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(versionOf('DOC-000002')).toBe('1.0');
    },
    SlowEnvTimeout,
  );

  it(
    'без сортування і фільтра в запиті немає ні sort, ні templateVersions',
    async () => {
      mockFetch();
      show('/?periodKey=202609');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(lastListed().has('sort')).toBe(false);
      expect(lastListed().has('templateVersions')).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'клік по заголовку шле sort на сервер (asc → desc → без), `aria-sort` і адреса оновлюються; порядок - з відповіді сервера',
    async () => {
      // «Сервер» навмисно віддає порядок, якого клієнтське сортування не дало б: 3, 4, 1, 2.
      mockFetch((params) =>
        params.get('sort') === 'templateVersion'
          ? [Items[2]!, Items[3]!, Items[0]!, Items[1]!]
          : params.get('sort') === '-templateVersion'
            ? [Items[1]!, Items[0]!, Items[3]!, Items[2]!]
            : Items,
      );
      show('/?periodKey=202609');
      const user = userEvent.setup();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual(['DOC-000001', 'DOC-000002', 'DOC-000003', 'DOC-000004']);

      const sortButton = document.querySelector<HTMLButtonElement>('[data-version-sort]');
      expect(sortButton).not.toBeNull();

      await user.click(sortButton!);
      await waitFor(() => expect(lastListed().get('sort')).toBe('templateVersion'), { timeout: SlowEnvTimeout });
      await waitFor(() => expect(rowKeys()).toEqual(['DOC-000003', 'DOC-000004', 'DOC-000001', 'DOC-000002']), {
        timeout: SlowEnvTimeout,
      });
      expect(screen.getByTestId('location').textContent).toContain('vsort=asc');
      expect(document.querySelector('[data-column="version"]')?.getAttribute('aria-sort')).toBe('ascending');

      await user.click(document.querySelector<HTMLButtonElement>('[data-version-sort]')!);
      await waitFor(() => expect(lastListed().get('sort')).toBe('-templateVersion'), { timeout: SlowEnvTimeout });
      await waitFor(() => expect(rowKeys()).toEqual(['DOC-000002', 'DOC-000001', 'DOC-000004', 'DOC-000003']), {
        timeout: SlowEnvTimeout,
      });
      expect(document.querySelector('[data-column="version"]')?.getAttribute('aria-sort')).toBe('descending');

      await user.click(document.querySelector<HTMLButtonElement>('[data-version-sort]')!);
      await waitFor(() => expect(lastListed().has('sort')).toBe(false), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );

  it(
    'сортування з адреси читається при відкритті і йде в перший же запит; зміна скидає cursor',
    async () => {
      mockFetch();
      show('/?periodKey=202609&vsort=desc&cursor=abc');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(listed[0]).toContain('sort=-templateVersion');

      const user = userEvent.setup();
      await user.click(document.querySelector<HTMLButtonElement>('[data-version-sort]')!);
      await waitFor(() => expect(screen.getByTestId('location').textContent).not.toContain('cursor='), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );

  it(
    'фільтр за версіями (з адреси) йде на сервер як templateVersions; клієнт відповідь не відфільтровує; «Clear filters» його знімає',
    async () => {
      // «Сервер» віддає лише документи 2 і 4; клієнтський фільтр залишив би ті самі, а ось 1 і 3 у відповіді немає.
      mockFetch((params) => (params.get('templateVersions') === '1.0,3.0' ? [Items[1]!, Items[3]!] : Items));
      show('/?periodKey=202609&versions=1.0,3.0');
      const user = userEvent.setup();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(listed[0]).toContain('templateVersions=1.0%2C3.0');
      expect(rowKeys()).toEqual(['DOC-000002', 'DOC-000004']);

      await user.click(
        await screen.findByRole('button', { name: '⟦documents.clearFilters⟧' }, { timeout: SlowEnvTimeout }),
      );
      await waitFor(() => expect(rowKeys()).toHaveLength(4), { timeout: SlowEnvTimeout });
      expect(lastListed().has('templateVersions')).toBe(false);
      expect(screen.getByTestId('location').textContent).toBe('?periodKey=202609');
    },
    SlowEnvTimeout,
  );

  it(
    'сервер повернув рядки, яких клієнтський фільтр не пропустив би, - вони показуються (фільтр не дублюється на клієнті)',
    async () => {
      mockFetch(() => [Items[2]!]);
      show('/?periodKey=202609&versions=9.9');

      await screen.findByText('DOC-000003', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual(['DOC-000003']);
    },
    SlowEnvTimeout,
  );

  it(
    'варіанти фільтра — усі версії з template-versions у порядку сервера (2.10 після 2.0, хоча на сторінці її немає); запит без templateVersions',
    async () => {
      mockFetch();
      show('/?periodKey=202609');
      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });

      const labels = await versionOptionLabels();
      expect(labels).toHaveLength(4);
      expect(labels[0]).toContain('version=1.0');
      expect(labels[1]).toContain('version=2.0');
      expect(labels[2]).toContain('version=2.10');
      expect(labels[3]).toContain('version=3.0');
      expect(optionRequests).toHaveLength(1);
      const params = new URL(optionRequests[0] ?? '/', 'http://localhost').searchParams;
      expect(params.get('periodKey')).toBe('202609');
      expect(params.has('templateVersions')).toBe(false);
      expect(params.has('sort')).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'обрана версія, якої сервер уже не віддає, лишається у варіантах (її є чим зняти)',
    async () => {
      mockFetch((params) => (params.has('templateVersions') ? [Items[0]!] : Items));
      show('/?periodKey=202609&versions=9.9');
      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });

      const labels = await versionOptionLabels();
      expect(labels.some((label) => label.includes('version=9.9'))).toBe(true);
      expect(labels.some((label) => label.includes('version=2.10'))).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'коли template-versions відмовив, варіанти — версії завантаженої сторінки (фільтр не зникає)',
    async () => {
      mockFetch();
      optionsAnswer = null;
      try {
        show('/?periodKey=202609');
        await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });

        const labels = await versionOptionLabels();
        expect(labels.map((label) => /version=([\d.]+)/.exec(label)?.[1])).toEqual(['1.0', '2.0', '3.0']);
      } finally {
        optionsAnswer = {
          items: [
            { version: '1.0', count: 2 },
            { version: '2.0', count: 1 },
            { version: '2.10', count: 5 },
            { version: '3.0', count: 1 },
          ],
          withoutVersionCount: 1,
        };
      }
    },
    SlowEnvTimeout,
  );

  it(
    'фільтр, за яким сервер нічого не віддав, дає «нічого не знайшлося», а не порожню таблицю',
    async () => {
      mockFetch((params) => (params.has('templateVersions') ? [] : Items));
      show('/?periodKey=202609&versions=9.9');

      await screen.findByText('⟦documents.noMatch⟧', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual([]);
      expect(within(document.body).queryByText('DOC-000001')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
