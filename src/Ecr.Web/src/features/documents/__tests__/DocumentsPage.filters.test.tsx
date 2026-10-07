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

const Late = { ...base, id: 1, businessKey: 'LATE-0001', hasLateEdits: true };
const OnTime = { ...base, id: 2, businessKey: 'ONTIME-0002', hasLateEdits: false };

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

/** Адреси запитів переліку (без `/summary`). */
const listed: string[] = [];

type ListReply = 'rows' | 'empty' | 'forbidden';

function mockFetch(reply: ListReply): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/documents/summary')) {
        return json({ draft: 1, submitted: 1, approved: 1, rejected: 0, withIssues: 0 });
      }

      if (url.includes('/api/v1/documents')) {
        listed.push(url);
        if (reply === 'forbidden') return json(Forbidden, 403);
        return json({ items: reply === 'rows' ? [Late, OnTime] : [], nextCursor: null, totalCount: null });
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
  search = '';
  withProject = false;
});

/** Межа тесту — з запасом на повний прогін; очікування — коротше, щоб червоне приходило швидко. */
const Slow = 120_000;
const Find = 45_000;

const stateField = (): Promise<HTMLElement> =>
  screen.findByRole('combobox', { name: '⟦documents.state⟧' }, { timeout: Find });

const ownersField = (): Promise<HTMLElement> =>
  screen.findByRole('combobox', { name: '⟦documents.owners⟧' }, { timeout: Find });

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

      const owners = await ownersField();
      fireEvent.change(owners, { target: { value: 'mine' } });

      await waitFor(() => expect(lastListed().get('mine')).toBe('true'), { timeout: Find });
      expect(new URLSearchParams(search).get('mine')).toBe('true');

      fireEvent.change(owners, { target: { value: '' } });

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
});
