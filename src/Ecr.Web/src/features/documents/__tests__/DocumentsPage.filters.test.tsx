import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Фільтри переліку документів (`BE-09b`): стан, «мої», позначка пізніх правок
 * і три різні порожні стани.
 *
 * ⚠ Перевіряється АДРЕСА ЗАПИТУ, а не лише розмітка: фільтр, який малює
 * вибране значення, але не доносить його до сервера, виглядає робочим і мовчки
 * показує весь перелік.
 *
 * ⚠ Рядки — ключами в `⟦…⟧`: каталогу в цьому тесті немає, і саме так `t()`
 * позначає промах. Ключі — ті самі, що треба завести в `09-seed.sql`.
 */

const base = { createdAt: '2026-01-01T00:00:00Z', projectId: 1, sheetCount: 1, sheetStates: {} };

// ✎ D-450: власник у рядку — ім'я та ідентифікатор; `null` — людини немає або доступ звужено («—»).
const Late = { ...base, id: 1, businessKey: 'LATE-0001', hasLateEdits: true, ownerUserId: 7, ownerDisplayName: 'Olena Koval' };
const OnTime = { ...base, id: 2, businessKey: 'ONTIME-0002', hasLateEdits: false, ownerUserId: null, ownerDisplayName: null };
const Stale = {
  ...base,
  id: 3,
  businessKey: 'STALE-0003',
  hasLateEdits: false,
  resultsStale: true,
  resultsStaleSince: '2026-01-05T10:00:00Z',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

const Forbidden = {
  type: 'about:blank',
  title: 'Forbidden',
  status: 403,
  detail: null,
  errorCode: 'ECR-AUTH-0403',
  correlationId: 'cid-documents-filter-1',
  messageKey: null,
};

/** Адреси запитів переліку (без `/summary` і `/owners`). */
const listed: string[] = [];

/** ✎ D-450: адреси запитів вибору власника (`GET /documents/owners`). */
const ownerRequests: string[] = [];

/** ✎ D-450: люди, яких віддає вибір власника. */
const Owners = {
  items: [
    { userId: 7, displayName: 'Olena Koval', documentCount: 12 },
    { userId: 9, displayName: 'Taras Melnyk', documentCount: 3 },
  ],
};

type ListReply = 'rows' | 'empty' | 'forbidden';

function mockFetch(reply: ListReply): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/documents/owners')) {
        ownerRequests.push(url);
        return json(Owners);
      }

      if (url.includes('/api/v1/documents/summary')) {
        return json({ draft: 1, submitted: 1, approved: 1, rejected: 0, withIssues: 0, staleResultsCount: 7, staleResultsMineCount: 2 });
      }

      if (url.includes('/api/v1/documents')) {
        listed.push(url);
        if (reply === 'forbidden') return json(Forbidden, 403);
        return json({ items: reply === 'rows' ? [Late, OnTime, Stale] : [], nextCursor: null, totalCount: null });
      }

      if (url.includes('/api/v1/projects')) {
        return json(
          withProject
            ? { items: [{ id: 1, code: 'AIR', status: 'Active' }], nextCursor: null, totalCount: 1 }
            : { items: [], nextCursor: null, totalCount: 0 },
        );
      }

      return json(null);
    }),
  );
}

let withProject = false;
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

afterEach(() => {
  vi.unstubAllGlobals();
  listed.length = 0;
  ownerRequests.length = 0;
  search = '';
  withProject = false;
});

/** Межа тесту — з запасом на повний прогін; очікування — коротше, щоб червоне приходило швидко. */
const Slow = 120_000;
const Find = 45_000;

const stateField = (): Promise<HTMLElement> =>
  screen.findByRole('combobox', { name: '⟦documents.state⟧' }, { timeout: Find });

// ✎ D-450: «Власник» — `Select` з пошуком (Mantine), а не `NativeSelect`; поле — за підписом.
const ownersField = (): Promise<HTMLElement> => screen.findByLabelText('⟦documents.owner⟧', {}, { timeout: Find });

/** Відкриває список «Власник» і обирає пункт за видимим ім'ям. */
async function pickOwner(name: string | RegExp): Promise<void> {
  fireEvent.click(await ownersField());
  fireEvent.click(await screen.findByRole('option', { name }, { timeout: Find }));
}

const lateEditsSwitch = (): Promise<HTMLElement> =>
  screen.findByRole('switch', { name: '⟦documents.filterLateEdits⟧' }, { timeout: Find });

describe('DocumentsPage: фільтри переліку (BE-09b)', () => {
  it(
    'дзеркало: фільтри не вибрано — запит без state і без mine',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      await screen.findByRole('table', {}, { timeout: Find });

      expect(listed.length).toBeGreaterThan(0);
      expect(lastListed().get('periodKey')).toBe('202601');
      expect(lastListed().has('state')).toBe(false);
      expect(lastListed().has('mine')).toBe(false);
      expect(lastListed().has('hasLateEdits')).toBe(false);
    },
    Slow,
  );

  it(
    'вибраний стан іде в адресу сторінки і в запит; курсор скидається',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601&cursor=abc');

      fireEvent.change(await stateField(), { target: { value: 'Rejected' } });

      await waitFor(() => expect(lastListed().get('state')).toBe('Rejected'), { timeout: Find });
      expect(lastListed().get('periodKey')).toBe('202601');
      expect(lastListed().has('cursor')).toBe(false);
      expect(new URLSearchParams(search).get('state')).toBe('Rejected');
    },
    Slow,
  );

  it(
    'стан з адреси відкриває вже відфільтрований перелік',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601&state=Submitted');

      expect(((await stateField()) as HTMLSelectElement).value).toBe('Submitted');
      await waitFor(() => expect(lastListed().get('state')).toBe('Submitted'), { timeout: Find });
    },
    Slow,
  );

  it(
    'без періоду фільтр стану недоступний, причина видима й прив\'язана до поля, state у запит не йде',
    async () => {
      mockFetch('rows');
      // ⚠ `state` в адресі без періоду — посилання, зібране руками. Сервер на
      // нього відповів би 422; екран мусить не дійти до цього.
      show('/?state=Rejected');

      const field = await stateField();
      await screen.findByRole('table', {}, { timeout: Find });

      expect((field as HTMLSelectElement).disabled).toBe(true);

      // ⚠ Причина двічі: прихований `description` поля, зв'язаний із ним, і
      // видима копія під рядом фільтрів (`aria-hidden`) — видимий опис під
      // полем опускав його нижню межу й розсував ряд (`FilterRow`).
      const reasons = screen.getAllByText('⟦documents.stateNeedsPeriod⟧');
      const reason = reasons.find((node) => node.id !== '');
      expect(reason).toBeDefined();
      expect(field.getAttribute('aria-describedby') ?? '').toContain(reason?.id ?? '⟂');
      expect(reasons.some((node) => node.getAttribute('aria-hidden') === 'true')).toBe(true);

      expect(listed.every((url) => !new URLSearchParams(url.split('?')[1] ?? '').has('state'))).toBe(true);
    },
    Slow,
  );

  it(
    'з періодом фільтр стану доступний і причини під ним немає',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      expect(((await stateField()) as HTMLSelectElement).disabled).toBe(false);
      expect(screen.queryByText('⟦documents.stateNeedsPeriod⟧')).toBeNull();
    },
    Slow,
  );

  it(
    '«лише мої» — вибір «Owners»: mine=true в адресі й у запиті, і знімається назад',
    async () => {
      mockFetch('rows');
      show('/');

      await pickOwner('⟦documents.filterMine⟧');

      await waitFor(() => expect(lastListed().get('mine')).toBe('true'), { timeout: Find });
      expect(new URLSearchParams(search).get('mine')).toBe('true');

      await pickOwner('⟦documents.ownersAll⟧');

      await waitFor(() => expect(new URLSearchParams(search).has('mine')).toBe(false), { timeout: Find });
      await waitFor(() => expect(lastListed().has('mine')).toBe(false), { timeout: Find });
    },
    Slow,
  );

  it(
    '«пізні правки» — перемикач: hasLateEdits=true в адресі й у запиті, працює БЕЗ періоду, і знімається назад',
    async () => {
      mockFetch('rows');
      // ⚠ Навмисно без `periodKey`: на відміну від фільтра стану, цей
      // перемикач діє за будь-який період і не має бути вимкненим.
      show('/');

      const toggle = await lateEditsSwitch();
      expect((toggle as HTMLInputElement).disabled).toBe(false);

      fireEvent.click(toggle);

      await waitFor(() => expect(lastListed().get('hasLateEdits')).toBe('true'), { timeout: Find });
      expect(new URLSearchParams(search).get('hasLateEdits')).toBe('true');
      expect((toggle as HTMLInputElement).checked).toBe(true);

      fireEvent.click(toggle);

      await waitFor(() => expect(new URLSearchParams(search).has('hasLateEdits')).toBe(false), { timeout: Find });
      await waitFor(() => expect(lastListed().has('hasLateEdits')).toBe(false), { timeout: Find });
    },
    Slow,
  );

  it(
    '«пізні правки» нічого не знайшли — «фільтр нічого не знайшов», кнопка скидання знімає й цей фільтр',
    async () => {
      mockFetch('empty');
      show('/?hasLateEdits=true');

      await screen.findByText('⟦documents.noMatch⟧', {}, { timeout: Find });
      expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();

      fireEvent.click(screen.getByRole('button', { name: '⟦documents.resetFilters⟧' }));

      await waitFor(() => expect(new URLSearchParams(search).has('hasLateEdits')).toBe(false), { timeout: Find });
      await waitFor(() => expect(lastListed().has('hasLateEdits')).toBe(false), { timeout: Find });
    },
    Slow,
  );

  it(
    'позначка пізніх правок — лише в рядку з hasLateEdits',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      const table = await screen.findByRole('table', {}, { timeout: Find });
      const late = within(table).getByRole('row', { name: /LATE-0001/ });
      const onTime = within(table).getByRole('row', { name: /ONTIME-0002/ });

      expect(within(late).queryByText('⟦documents.lateEdits⟧')).not.toBeNull();
      expect(within(onTime).queryByText('⟦documents.lateEdits⟧')).toBeNull();
    },
    Slow,
  );

  it(
    '(а) фільтр нічого не знайшов — свій заголовок і кнопка скидання, що знімає фільтри',
    async () => {
      mockFetch('empty');
      show('/?periodKey=202601&state=Rejected&mine=true');

      await screen.findByText('⟦documents.noMatch⟧', {}, { timeout: Find });
      expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();

      fireEvent.click(screen.getByRole('button', { name: '⟦documents.resetFilters⟧' }));

      await waitFor(() => expect(new URLSearchParams(search).has('state')).toBe(false), { timeout: Find });
      const params = new URLSearchParams(search);
      expect(params.has('mine')).toBe(false);
      // Період належить екрану, а не фільтрам: скидання його не чіпає.
      expect(params.get('periodKey')).toBe('202601');

      await waitFor(() => expect(lastListed().has('state')).toBe(false), { timeout: Find });
      expect(lastListed().has('mine')).toBe(false);
    },
    Slow,
  );

  it(
    '(б) документів немає взагалі — «документів немає», без кнопки скидання',
    async () => {
      // ✎ FP-3: проєкт є (доступ видано), але документів немає. Без жодного проєкту текст інший —
      // «немає доступу» (`DocumentsPage.noProjects.test.tsx`).
      withProject = true;
      mockFetch('empty');
      show('/?periodKey=202601');

      await screen.findByText('⟦documents.empty⟧', {}, { timeout: Find });
      expect(screen.queryByText('⟦documents.noMatch⟧')).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦documents.resetFilters⟧' })).toBeNull();
    },
    Slow,
  );

  it(
    '(в) відмова під фільтром — стан відмови, а не «нічого не знайшлося» (L10)',
    async () => {
      mockFetch('forbidden');
      show('/?periodKey=202601&mine=true');

      const alert = await screen.findByRole('alert', {}, { timeout: Find });

      expect(alert.textContent ?? '').toContain('ECR-AUTH-0403');
      expect(screen.queryByText('⟦documents.noMatch⟧')).toBeNull();
      expect(screen.queryByText('⟦documents.empty⟧')).toBeNull();
    },
    Slow,
  );
});

describe('DocumentsPage: пошук і фільтр проєкту (UI RC9, макет FilterBar)', () => {
  it(
    'пошук: набране йде в адресу одразу, а в запит `q` — після паузи; скидання знімає його',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      const field = await screen.findByRole('searchbox', { name: '⟦documents.search⟧' }, { timeout: Find });
      fireEvent.change(field, { target: { value: 'ONTIME' } });

      expect(new URLSearchParams(search).get('q')).toBe('ONTIME');
      await waitFor(() => expect(lastListed().get('q')).toBe('ONTIME'), { timeout: Find });

      fireEvent.click(await screen.findByRole('button', { name: '⟦documents.clearFilters⟧' }, { timeout: Find }));

      await waitFor(() => expect(new URLSearchParams(search).has('q')).toBe(false), { timeout: Find });
      await waitFor(() => expect(lastListed().has('q')).toBe(false), { timeout: Find });
    },
    Slow,
  );

  it(
    'фільтр проєкту: projectId в адресі й у запиті; без проєктів поля немає',
    async () => {
      withProject = true;
      mockFetch('rows');
      show('/?periodKey=202601');

      const select = await screen.findByRole('combobox', { name: '⟦documents.project⟧' }, { timeout: Find });
      await within(select).findByRole('option', { name: 'AIR' }, { timeout: Find });
      fireEvent.change(select, { target: { value: '1' } });

      await waitFor(() => expect(lastListed().get('projectId')).toBe('1'), { timeout: Find });
      expect(new URLSearchParams(search).get('projectId')).toBe('1');
    },
    Slow,
  );

  it(
    'порожній пошук і відсутній проєкт не потрапляють у запит',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      await screen.findByRole('table', {}, { timeout: Find });

      expect(lastListed().has('q')).toBe(false);
      expect(lastListed().has('projectId')).toBe(false);
      expect(screen.queryByRole('combobox', { name: '⟦documents.project⟧' })).toBeNull();
    },
    Slow,
  );
  it(
    'застарілі результати: бейдж лише в рядку з resultsStale=true; чіп з лічильником ставить resultsStale=true в запит',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      await screen.findByRole('table', {}, { timeout: Find });
      expect(document.querySelectorAll('[data-results-stale]')).toHaveLength(1);
      expect(lastListed().has('resultsStale')).toBe(false);

      const chip = await screen.findByRole('checkbox', { name: /documents.filterStale/ }, { timeout: Find });
      expect((chip as HTMLInputElement).disabled).toBe(false);

      fireEvent.click(chip);

      await waitFor(() => expect(lastListed().get('resultsStale')).toBe('true'), { timeout: Find });
      expect(new URLSearchParams(search).get('resultsStale')).toBe('true');
    },
    Slow,
  );

  it(
    'застарілі результати: без періоду чіп вимкнений, а resultsStale з адреси в запит не йде (422 на сервері)',
    async () => {
      mockFetch('rows');
      show('/?resultsStale=true');

      await screen.findByRole('table', {}, { timeout: Find });
      const chip = await screen.findByRole('checkbox', { name: /documents.filterStaleNoCount/ }, { timeout: Find });
      expect((chip as HTMLInputElement).disabled).toBe(true);
      expect(lastListed().has('resultsStale')).toBe(false);
    },
    Slow,
  );
});

describe('DocumentsPage: власник документа (D-450)', () => {
  it(
    'колонка «Власник»: ім\'я з відповіді сервера, а null — видиме «—»',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      await screen.findByRole('table', {}, { timeout: Find });
      expect(screen.getByRole('columnheader', { name: '⟦documents.owner⟧' })).not.toBeNull();

      const ownerCell = (key: string): HTMLElement | undefined =>
        Array.from(document.querySelectorAll<HTMLElement>(`[data-document-row="${key}"] [data-column="owner"]`))[0];

      expect(ownerCell('LATE-0001')?.textContent).toBe('Olena Koval');
      expect(ownerCell('ONTIME-0002')?.textContent).toBe('—');
    },
    Slow,
  );

  it(
    'перелік людей не читається з відкриттям сторінки — лише з відкриттям списку',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601');

      await screen.findByRole('table', {}, { timeout: Find });
      expect(ownerRequests).toHaveLength(0);

      fireEvent.click(await ownersField());

      await waitFor(() => expect(ownerRequests.length).toBeGreaterThan(0), { timeout: Find });
      // Той самий період, що в переліку: межі читача рахуються в ньому.
      expect(new URLSearchParams(ownerRequests[0]?.split('?')[1] ?? '').get('periodKey')).toBe('202601');
    },
    Slow,
  );

  it(
    'вибір людини: owner=<id> в адресі, ownerUserId у запиті, без mine; «Clear filters» знімає',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601&mine=true&cursor=abc');

      await pickOwner(/Taras Melnyk/);

      await waitFor(() => expect(lastListed().get('ownerUserId')).toBe('9'), { timeout: Find });
      expect(lastListed().has('mine')).toBe(false);
      expect(lastListed().has('cursor')).toBe(false);
      const params = new URLSearchParams(search);
      expect(params.get('owner')).toBe('9');
      expect(params.has('mine')).toBe(false);

      fireEvent.click(await screen.findByRole('button', { name: '⟦documents.clearFilters⟧' }, { timeout: Find }));

      await waitFor(() => expect(new URLSearchParams(search).has('owner')).toBe(false), { timeout: Find });
      await waitFor(() => expect(lastListed().has('ownerUserId')).toBe(false), { timeout: Find });
    },
    Slow,
  );

  it(
    'посилання з owner= відкриває вже відфільтрований перелік; старе mine=true поруч — не йде в запит (422)',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601&owner=7&mine=true');

      await waitFor(() => expect(lastListed().get('ownerUserId')).toBe('7'), { timeout: Find });
      expect(lastListed().has('mine')).toBe(false);

      // Ім'я обраної людини — з рядка сторінки (той самий ownerUserId), без окремого запиту людей.
      await waitFor(() => expect((screen.getByLabelText('⟦documents.owner⟧') as HTMLInputElement).value).toBe('Olena Koval'), {
        timeout: Find,
      });
    },
    Slow,
  );

  it(
    'owner= не додатне ціле — без фільтра, а не 422',
    async () => {
      mockFetch('rows');
      show('/?periodKey=202601&owner=abc');

      await screen.findByRole('table', {}, { timeout: Find });
      expect(lastListed().has('ownerUserId')).toBe(false);
      expect(screen.queryByRole('button', { name: '⟦documents.clearFilters⟧' })).toBeNull();
    },
    Slow,
  );
});
