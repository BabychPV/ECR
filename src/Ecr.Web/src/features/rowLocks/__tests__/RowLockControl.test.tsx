import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { JSX } from 'react';
import { apiFetch } from '@/api/client';
import type { TableSliceDto } from '@/api/types';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { publishFocus, resetFocus } from '@/features/grid/focusStore';
import { testTheme } from '@/test/render';
import { RowLockControl } from '../RowLockControl';
import { useRowLocks } from '../useRowLocks';

/**
 * ✎ B139 (B75): кнопки блокування рядків у сітці, діалог «Заблокувати рядки…» і перелік блокувань зі «Зняти».
 *
 * ⚠ Каталогу немає: тексти — ключі в маркерах (`⟦rowLock.lockAction⟧`). Сервер — `fetch`-стаб.
 */
const DocumentId = 96;
const PeriodKey = 202611;
const TableInstanceId = 501;
const Table = { sheetCode: 'S1', tableCode: 'T1', sheetCopyId: null };

const Slice = {
  columns: [],
  rows: [
    { rowKey: 'R1', label: 'Boiler 1' },
    { rowKey: 'R2', label: 'Boiler 2' },
  ],
} as unknown as TableSliceDto;

function lock(over: Partial<RowLockDto> = {}): RowLockDto {
  return {
    canUnlock: true,
    id: 5,
    isActive: true,
    lockFrom: '2026-11-01',
    lockUntil: '2026-11-30',
    note: 'audit',
    ownerDisplayName: 'A. Serikbayev',
    ownerUserId: 3,
    rowKey: 'R2',
    sheetCode: 'S1',
    sheetCopyId: null,
    tableCode: 'T1',
    ...over,
  };
}

interface Sent {
  readonly method: string;
  readonly url: string;
  readonly body: unknown;
}

let sent: Sent[] = [];

/** Картка документа в кеші (`GET /documents/{id}?periodKey=`): лише те, що читає кнопка. */
interface Card {
  readonly canLockRows?: boolean | null;
}

const CardKey = ['document', DocumentId, PeriodKey];

function show(permissions: string[], locks: RowLockDto[] = [], card?: Card): QueryClient {
  sent = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');
      const json = (body: unknown, status = 200): Response =>
        new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions, simulatedForUserId: null, userId: 9, userName: 'tester' });
      }
      if (url.includes('/row-locks') && method !== 'GET') {
        sent.push({ method, url, body: init?.body === undefined || init.body === null ? null : JSON.parse(String(init.body)) });
        return method === 'DELETE' ? json(null, 204) : json([lock({ id: 77, rowKey: 'R1' })], 201);
      }

      return json([]);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // ⚠ Картку кладе в кеш сторінка документа (`DocumentPage`); кнопка її лише читає й сама нічого не запитує.
  if (card !== undefined) client.setQueryData(CardKey, { id: DocumentId, ...card });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RowLockControl
          documentId={DocumentId}
          periodKey={PeriodKey}
          tableInstanceId={TableInstanceId}
          table={Table}
          slice={Slice}
          locks={locks}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

/** Сесія прочитана (`/me`), а кнопки «Заблокувати рядки…» немає. */
async function expectNoLockButton(): Promise<void> {
  await waitFor(() =>
    expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('/api/v1/me'))).toBe(true),
  );
  // Один оберт черги: відповідь `/me` дійшла до компонента (з правом кнопка вже була б).
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
  expect(screen.queryByRole('button', { name: LockButton })).toBeNull();
}

/** Рядок 0 у фокусі → «Заблокувати рядки…» → дати → «Заблокувати»: один POST з адресою таблиці. */
async function lockFocusedRowThroughDialog(): Promise<void> {
  act(() => publishFocus(TableInstanceId, PeriodKey, { rowIndex: 0, columnCode: 'A' } as never));

  const button = await screen.findByRole('button', { name: LockButton });
  await waitFor(() => expect((button as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(button);

  const dialog = await screen.findByRole('dialog');
  const save = await screen.findByRole('button', { name: /rowLock\.lockVerb/ });
  const [from, to] = Array.from(dialog.querySelectorAll('input')).filter((input) => input.type !== 'hidden');
  if (from === undefined || to === undefined) throw new Error('полів дат немає');
  fireEvent.change(from, { target: { value: '2026-11-10' } });
  fireEvent.blur(from);
  fireEvent.change(to, { target: { value: '2026-11-20' } });
  fireEvent.blur(to);
  await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(save);

  await waitFor(() => expect(sent).toHaveLength(1));
  expect(sent[0]?.method).toBe('POST');
  expect(sent[0]?.url).toContain(`/api/v1/documents/${String(DocumentId)}/row-locks`);
  expect((sent[0]?.body as { rowKeys?: string[] }).rowKeys).toEqual(['R1']);
}

const LockButton = /rowLock\.lockAction/;
const ListButton = /rowLock\.listAction/;

/*
 * ⛔ F13 (B139, пункт e): діалог і перелік — `lazy()`-чанки (`RowLockControl.tsx`), і діалог тягне `@mantine/dates`.
 * Без прогріву перший тест, що відкриває діалог, платив за імпорт і трансформацію чанка ВСЕРЕДИНІ власного
 * бюджету часу — під навантаженням CPU він не вкладався (`findByRole('dialog')` / таймаут тесту). Модулі
 * вантажаться тут один раз, до тестів; `lazy()` у компоненті далі бере їх із кешу модулів. Час хука — на імпорт,
 * бюджети самих тестів не змінено.
 */
beforeAll(async () => {
  await Promise.all([import('../RowLockDialog'), import('../RowLocksPanel')]);
}, 120_000);

afterEach(() => {
  resetFocus();
  vi.unstubAllGlobals();
});

describe('useRowLocks (B139)', () => {
  it('відповідь не масивом не валить сітку: блокувань немає', async () => {
    // ⛔ RED до фіксу: `renderFeedback` (сторінка документа з загальним моком `{}`) падала в рендері
    // «(rowLocks.data ?? []).filter is not a function».
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result } = renderHook(() => useRowLocks(DocumentId, PeriodKey), {
      wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual([]);
  });
});

describe('RowLockControl (B139)', () => {
  it('без права Document.RowLock і без блокувань — нічого', async () => {
    show(['Document.View']);

    await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: LockButton })).toBeNull();
    expect(screen.queryByRole('button', { name: ListButton })).toBeNull();
  });

  it('без права, але з блокуваннями — лише перелік (без «Заблокувати рядки…»)', async () => {
    show(['Document.View'], [lock({ canUnlock: false })]);

    fireEvent.click(await screen.findByRole('button', { name: ListButton }));
    // Назва рядка — підпис, а не технічний ключ; «Зняти» немає: сервер не дозволив (`canUnlock = false`).
    expect(await screen.findByText('Boiler 2')).toBeTruthy();
    expect(screen.queryByText('R2')).toBeNull();
    expect(screen.queryByRole('button', { name: /rowLock\.release/ })).toBeNull();
    expect(screen.queryByRole('button', { name: LockButton })).toBeNull();
  });

  it('з правом, без виділеного рядка — кнопка вимкнена з причиною для читалки', async () => {
    show(['Document.View', 'Document.RowLock']);

    const button = await screen.findByRole('button', { name: LockButton });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    const hint = document.getElementById(button.getAttribute('aria-describedby') ?? '');
    expect(hint?.textContent).toContain('rowLock.pickHint');
  });

  it('з правом і рядком у фокусі — діалог шле POST з адресою таблиці, рядком і датами', async () => {
    show(['Document.View', 'Document.RowLock']);
    act(() => publishFocus(TableInstanceId, PeriodKey, { rowIndex: 0, columnCode: 'A' } as never));

    const button = await screen.findByRole('button', { name: LockButton });
    await waitFor(() => expect((button as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(button);

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Boiler 1');
    const save = await screen.findByRole('button', { name: /rowLock\.lockVerb/ });
    // ⛔ Без дати «по» форма не йде: кнопка вимкнена.
    expect((save as HTMLButtonElement).disabled).toBe(true);

    const [from, to] = Array.from(dialog.querySelectorAll('input')).filter((input) => input.type !== 'hidden');
    if (from === undefined || to === undefined) throw new Error('полів дат немає');
    // Поле дати приймає лише `YYYY-MM-DD` (`StrictDateInput`).
    fireEvent.change(from, { target: { value: '2026-11-10' } });
    fireEvent.blur(from);
    fireEvent.change(to, { target: { value: '2026-11-20' } });
    fireEvent.blur(to);

    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]?.method).toBe('POST');
    expect(sent[0]?.url).toContain(`/api/v1/documents/${String(DocumentId)}/row-locks`);
    expect(sent[0]?.body).toEqual({
      periodKey: PeriodKey,
      sheetCode: 'S1',
      tableCode: 'T1',
      sheetCopyId: null,
      rowKeys: ['R1'],
      fromDate: '2026-11-10',
      toDate: '2026-11-20',
      note: null,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  /*
   * ✎ B143 (D-538): «Заблокувати рядки…» — за прапорцем картки документа `canLockRows` (рішення сервера: носій
   * права `Document.RowLock` або ВЛАСНИК документа з Write на таблицю; закритий період, архів, симуляція — ні).
   * Право з `/me` — лише запасне правило, доки прапорця немає (картка не дійшла, без періоду, старіший сервер).
   *
   * ⛔ Мутації (`RowLockControl.tsx`, `mayLockRows`): ігнорувати прапорець (лише право) — червоні (1) і (4);
   * `false` як «прапорця немає» — (4); кнопка завжди — (2), (4), (6); читати картку за іншим ключем — (1), (4), (7).
   */
  describe('B143: видимість за canLockRows картки документа', () => {
    it('(1) власник без права, canLockRows = true — кнопка є, діалог шле POST', async () => {
      show(['Document.View'], [], { canLockRows: true });

      await lockFocusedRowThroughDialog();
    });

    it('(2) не власник без права, canLockRows = false — кнопки немає', async () => {
      show(['Document.View'], [], { canLockRows: false });

      await expectNoLockButton();
    });

    it('(3) носій права, canLockRows = true — кнопка є', async () => {
      show(['Document.View', 'Document.RowLock'], [], { canLockRows: true });

      expect(await screen.findByRole('button', { name: LockButton })).toBeTruthy();
    });

    it('(4) носій права, canLockRows = false (закритий період) — кнопки немає, перелік лишається', async () => {
      show(['Document.View', 'Document.RowLock'], [lock({ canUnlock: false })], { canLockRows: false });

      await expectNoLockButton();
      expect(screen.getByRole('button', { name: ListButton })).toBeTruthy();
    });

    it.each([
      { name: 'картки в кеші немає', card: undefined },
      { name: 'картка без прапорця (старіший сервер)', card: {} },
      { name: 'canLockRows = null (картка без періоду)', card: { canLockRows: null } },
    ])('(5) прапорця немає ($name) + право — кнопка є (запасне правило)', async ({ card }) => {
      show(['Document.View', 'Document.RowLock'], [], card);

      expect(await screen.findByRole('button', { name: LockButton })).toBeTruthy();
    });

    it.each([
      { name: 'картки в кеші немає', card: undefined },
      { name: 'canLockRows = null (картка без періоду)', card: { canLockRows: null } },
    ])('(6) прапорця немає ($name), права немає — кнопки немає', async ({ card }) => {
      show(['Document.View'], [], card);

      await expectNoLockButton();
    });

    it('(7) картка дійшла пізніше за сітку — кнопка з’являється без нового запиту картки', async () => {
      const client = show(['Document.View']);
      await expectNoLockButton();

      act(() => {
        client.setQueryData(CardKey, { id: DocumentId, canLockRows: true });
      });

      expect(await screen.findByRole('button', { name: LockButton })).toBeTruthy();
      // ⛔ Другого запиту картки немає: її вантажить сторінка документа.
      const cardRequests = vi.mocked(fetch).mock.calls.filter(([input]) =>
        /\/api\/v1\/documents\/\d+(\?|$)/.test(String(input)),
      );
      expect(cardRequests).toHaveLength(0);
    });

    it('(8) поруч із запитом сторінки: інвалідація картки перечитує її ОДИН раз, кнопка йде за новим прапорцем', async () => {
      // Сторінка документа тримає запит картки (з `queryFn`), кнопка — лише підписку на той самий ключ.
      let served = 0;
      let flag = false;
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL) => {
          const url = String(input);
          const json = (body: unknown): Response =>
            new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
          if (url.includes('/api/v1/me')) {
            return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: ['Document.View'], simulatedForUserId: null, userId: 9, userName: 'tester' });
          }
          if (/\/api\/v1\/documents\/\d+\?periodKey=/.test(url)) {
            served += 1;

            return json({ id: DocumentId, canLockRows: flag });
          }

          return json([]);
        }),
      );

      function Page(): JSX.Element {
        useQuery({
          queryKey: CardKey,
          queryFn: () => apiFetch(`/api/v1/documents/${String(DocumentId)}?periodKey=${String(PeriodKey)}`),
        });

        return <RowLockControl documentId={DocumentId} periodKey={PeriodKey} tableInstanceId={TableInstanceId} table={Table} slice={Slice} locks={[]} />;
      }

      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={client}>
            <Page />
          </QueryClientProvider>
        </MantineProvider>,
      );

      await waitFor(() => expect(served).toBe(1));
      await expectNoLockButton();

      flag = true;
      await act(async () => {
        await client.invalidateQueries({ queryKey: CardKey });
      });

      expect(await screen.findByRole('button', { name: LockButton })).toBeTruthy();
      expect(served).toBe(2);
    });
  });

  it('перелік: «Зняти» шле DELETE саме цього блокування', async () => {
    show(['Document.View'], [lock({ id: 5 }), lock({ id: 6, rowKey: 'R1', canUnlock: false })]);

    fireEvent.click(await screen.findByRole('button', { name: ListButton }));
    const release = await screen.findAllByRole('button', { name: /rowLock\.release/ });
    // Одна кнопка: у другого блокування `canUnlock = false`.
    expect(release).toHaveLength(1);
    fireEvent.click(release[0] as HTMLElement);

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]?.method).toBe('DELETE');
    expect(sent[0]?.url).toMatch(/\/row-locks\/5$/);
  });
});
