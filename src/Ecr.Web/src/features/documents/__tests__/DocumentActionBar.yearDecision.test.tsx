import { createElement, type JSX, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';
import { gridColumns } from '@/features/grid/DocumentGrid';
import { NoLocalFlags } from '@/features/grid/cellState';
import { pickDecisionMonth, useDecisionView } from '@/features/workflow/decisionTarget';
import type { ColumnDto, TableSliceDto } from '@/api/types';

/**
 * D-6: документ з розкладкою «рік» у місячному проєкті — одна сітка на 12 місяців, стан погодження в кожного місяця
 * свій; сторінка стоїть на якорі (січень). Approve/Reject/Return for edits/Recall беруть ЦІЛЬОВИЙ місяць
 * (`submission-target.monthColumns`), як подання — свій (D-441): кнопка називає місяць, ▾ перелічує місяці, посилання
 * «Review» черги (`decideMonth`) і позначка стану місяця в заголовку колонки задають ціль.
 *
 * ⛔ На старому коді кнопки дивилися лише на стан якоря: квітень поданий, січень — чернетка → «Approve» не було взагалі.
 * Мутація: у `useSheetActions` замінити `decidePeriod` у виклику `ask(...)` на `periodKey` — тести з POST червоні;
 * прибрати `onClick` з кнопки-позначки в `gridColumns` — тести позначки червоні.
 */

const Jan = 202601;
const Mar = 202603;
const Apr = 202604;
const May = 202605;
const Table = 5;

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Approve' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View', 'Document.Reopen'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'approver',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const Codes: Record<number, string> = { 1: 'JAN', 2: 'FEB', 3: 'MAR', 4: 'APR', 5: 'MAY', 6: 'JUN' };

interface Post {
  readonly url: string;
  readonly body: { periodKey?: number; approved?: boolean };
}

let posts: Post[] = [];
let gets: string[] = [];

/** `statuses`: статус за номером місяця; решта (1..6) — `Draft`. */
function mockFetch(statuses: Record<number, string>): void {
  posts = [];
  gets = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      gets.push(url);

      if (url.includes('/approver-edit')) return json({ canApproverEdit: true });
      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/submission-target')) {
        return json({
          targetPeriodKey: null,
          nextPeriodKey: null,
          periods: [],
          monthColumns: Object.entries(Codes).map(([month, code]) => ({
            tableDefId: Table,
            columnCode: code,
            monthNumber: Number(month),
            periodKey: 202600 + Number(month),
            sheetStatus: statuses[Number(month)] ?? 'Draft',
          })),
        });
      }

      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) as Post['body'] });

        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) return json([]);

      return json(null);
    }),
  );
}

interface ShowOptions {
  readonly state?: string;
  readonly yearLayout?: boolean;
  readonly decideMonth?: number | null;
  readonly extra?: JSX.Element;
}

function show(statuses: Record<number, string>, options: ShowOptions = {}): void {
  mockFetch(statuses);
  const state = options.state ?? 'Draft';

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, Jan], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: { S1: state },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentActionBar
          documentId={1}
          periodKey={Jan}
          sheetDefId={42}
          sheetCode="S1"
          sheetName="Water"
          state={state}
          lock={null}
          readOnly={false}
          language="en"
          canImport={false}
          canExport={false}
          validate={{ loading: false, run: () => undefined }}
          documentItems={[]}
          yearLayout={options.yearLayout ?? true}
          decideMonth={options.decideMonth ?? null}
        />
        {options.extra}
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const approveButton = (month: string): Promise<HTMLElement> =>
  screen.findByRole('button', { name: new RegExp(`workflow\\.approvePeriod \\(period=${month}\\)`) });

const approvePosts = (): Post[] => posts.filter((p) => p.url.endsWith('/approve'));

describe('річний документ: Approve над поданим місяцем (D-6)', () => {
  it('квітень поданий, січень — чернетка: «Approve: April 2026», запити — за 202604, не за якорем', async () => {
    show({ 4: 'Submitted' });

    const approve = await approveButton('April 2026');
    expect(approve.getAttribute('data-period-key')).toBe(String(Apr));
    expect(screen.getByRole('button', { name: /workflow\.rejectPeriod \(period=April 2026\)/ })).toBeTruthy();

    fireEvent.click(approve);
    // Діалог називає місяць.
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.approvePeriodTitle \(period=April 2026\)/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.approve⟧$/ }));

    await waitFor(() => expect(approvePosts()).toHaveLength(1));
    expect(approvePosts()[0]?.body).toMatchObject({ periodKey: Apr, approved: true });
    // Право погоджувача — для цільового місяця; вплив (діалог впливу) — теж за ним.
    expect(gets.some((u) => u.includes('/approver-edit') && u.includes(`periodKey=${String(Apr)}`))).toBe(true);
    expect(gets.some((u) => u.includes('/approver-edit') && u.includes(`periodKey=${String(Jan)}`))).toBe(false);
    expect(gets.some((u) => u.includes('/source-impact') && u.includes(`periodKey=${String(Apr)}`))).toBe(true);
  });

  it('Reject — той самий місяць; діалог причини називає його, запит несе 202604', async () => {
    show({ 4: 'Submitted' });

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.rejectPeriod \(period=April 2026\)/ }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.rejectPeriodTitle \(period=April 2026\)/)).toBeTruthy();

    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'wrong figures' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.reject⟧$/ }));

    await waitFor(() => expect(approvePosts()).toHaveLength(1));
    expect(approvePosts()[0]?.body).toMatchObject({ periodKey: Apr, approved: false });
  });

  it('«Return for edits» — найраніший ЗАТВЕРДЖЕНИЙ місяць; діалог називає його, запит несе 202603', async () => {
    show({ 3: 'Approved', 4: 'Submitted' });

    const reopen = await screen.findByRole('button', { name: /workflow\.reopenPeriod \(period=March 2026\)/ });
    expect(reopen.getAttribute('data-period-key')).toBe(String(Mar));
    // Approve при цьому — для поданого квітня.
    expect(await approveButton('April 2026')).toBeTruthy();

    fireEvent.click(reopen);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.reopenPeriodTitle \(period=March 2026\)/)).toBeTruthy();
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'revise' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.reopen⟧$/ }));

    await waitFor(() => expect(posts.filter((p) => p.url.endsWith('/reopen'))).toHaveLength(1));
    expect(posts.find((p) => p.url.endsWith('/reopen'))?.body).toMatchObject({ periodKey: Mar });
  });

  it('банер стану описує цільовий місяць, а не якір', async () => {
    show({ 4: 'Submitted' });

    const banner = await screen.findByTestId('document-sheet-banner');
    expect(banner.textContent).toContain('document.banner.submitted');
    expect(banner.textContent).toContain('April 2026');
  });
});

describe('вибір місяця: ▾ і посилання «Review» (D-6)', () => {
  it('▾ перелічує подані місяці, вибір перемикає ціль: підпис і запит — травень', async () => {
    show({ 4: 'Submitted', 5: 'Submitted' });

    const approve = await approveButton('April 2026');
    fireEvent.click(await screen.findByRole('button', { name: '⟦workflow.pickApprovePeriod⟧' }));
    const may = await waitFor(() => {
      const item = document.querySelector<HTMLElement>(`[data-decision-period="${String(May)}"]`);
      expect(item).not.toBeNull();
      expect(document.querySelector(`[data-decision-period="${String(Jan)}"]`)).toBeNull();

      return item as HTMLElement;
    });
    fireEvent.click(may);

    const mayApprove = await approveButton('May 2026');
    expect(mayApprove.getAttribute('data-period-key')).toBe(String(May));
    expect(approve.isConnected ? approve.textContent : mayApprove.textContent).toContain('period=May 2026');

    fireEvent.click(mayApprove);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.approve⟧$/ }));
    await waitFor(() => expect(approvePosts()).toHaveLength(1));
    expect(approvePosts()[0]?.body).toMatchObject({ periodKey: May });
  });

  it('посилання з `decide=202605` заздалегідь вибирає травень, хоча квітень теж чекає', async () => {
    show({ 4: 'Submitted', 5: 'Submitted' }, { decideMonth: May });

    const approve = await approveButton('May 2026');
    expect(approve.getAttribute('data-period-key')).toBe(String(May));
  });

  it('`decide` на місяць, що не чекає затвердження, ігнорується — найраніший поданий', async () => {
    show({ 4: 'Submitted', 5: 'Submitted' }, { decideMonth: 202606 });

    expect((await approveButton('April 2026')).getAttribute('data-period-key')).toBe(String(Apr));
  });
});

describe('звичайний документ без розкладки «рік» — без змін', () => {
  it('поданий якір: «Approve» без місяця, без ▾, запит — за періодом сторінки', async () => {
    show({ 4: 'Submitted' }, { state: 'Submitted', yearLayout: false });

    const approve = await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    expect(approve.getAttribute('data-period-key')).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦workflow.pickApprovePeriod⟧' })).toBeNull();
    expect(screen.queryByRole('button', { name: /workflow\.approvePeriod/ })).toBeNull();

    fireEvent.click(approve);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('⟦workflow.approveTitle⟧')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.approve⟧$/ }));

    await waitFor(() => expect(approvePosts()).toHaveLength(1));
    expect(approvePosts()[0]?.body).toMatchObject({ periodKey: Jan });
  });
});

// ── Заголовок місячної колонки: підсвітка цілі й позначка-кнопка ──────────────────────────────

function column(code: string, header: string): ColumnDto {
  return {
    id: 1,
    code,
    header,
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
  } as ColumnDto;
}

const headerSlice = {
  tableInstanceId: 1,
  periodKey: Jan,
  columns: Object.values(Codes).map((code) => column(code, code)),
  rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false }],
  cellPermissions: {},
  cellConfirmations: {},
} as unknown as TableSliceDto;

/** Заголовки місячних колонок так, як їх збирає сітка, зі справжнім сховищем цілі (`useDecisionView`). */
function Headers({ statuses }: { readonly statuses: Record<number, string> }): JSX.Element {
  const view = useDecisionView(1, 42);
  const marked = new Map<string, 'Submitted' | 'Approved'>();
  const periodOf = new Map<string, { periodKey: number }>();
  for (const [month, code] of Object.entries(Codes)) {
    periodOf.set(code, { periodKey: 202600 + Number(month) });
    const status = statuses[Number(month)];
    if (status === 'Submitted' || status === 'Approved') marked.set(code, status);
  }

  const columns = gridColumns(
    headerSlice,
    false,
    NoLocalFlags,
    {},
    { blocked: new Map(), warning: new Map() },
    new Map(),
    new Map(),
    new Map(),
    new Set(),
    null,
    null,
    new Set(),
    new Map(),
    marked,
    new Map(),
    new Map(),
    Table,
    { view, periodOf, choose: (month, kind) => pickDecisionMonth(1, 42, month, kind) },
  );

  // Мінімальний `h` для React: `class` → `className`, обробники лишаються.
  const h = (tag: string, props: Record<string, unknown> | null, children?: unknown): JSX.Element => {
    const { class: className, ...rest } = props ?? {};
    const kids = (Array.isArray(children) ? children : children === undefined ? [] : [children]) as ReactNode[];

    return createElement(tag, { ...rest, className }, ...kids);
  };

  return (
    <div>
      {columns.map((c) => {
        const template = c.columnTemplate as unknown as ((create: typeof h) => JSX.Element) | undefined;
        const props = (c.columnProperties as unknown as (() => Record<string, unknown>) | undefined)?.() ?? {};

        return (
          <div
            key={String(c.prop)}
            data-col={String(c.prop)}
            data-decision-target={props['data-decision-target'] as string | undefined}
            className={String(props['class'] ?? '')}
          >
            {template === undefined ? String(c.name) : template(h)}
          </div>
        );
      })}
    </div>
  );
}

describe('заголовок місячної колонки (D-6)', () => {
  const statuses = { 4: 'Submitted', 5: 'Submitted' };
  const col = (code: string): HTMLElement => document.querySelector<HTMLElement>(`[data-col="${code}"]`) as HTMLElement;

  it('колонка-ціль підсвічена; позначка доступного місяця — кнопка з назвою; чернетка — без кнопки', async () => {
    show(statuses, { extra: <Headers statuses={statuses} /> });
    await approveButton('April 2026');

    await waitFor(() => expect(col('APR').className).toContain('ecr-header-decision-target'));
    expect(col('APR').getAttribute('data-decision-target')).toBe('true');
    expect(col('MAY').className).not.toContain('ecr-header-decision-target');
    expect(col('JAN').className).not.toContain('ecr-header-decision-target');

    // Кнопка з доступною назвою — на поданих місяцях; на січні (чернетка) позначки немає зовсім.
    const aprChip = within(col('APR')).getByRole('button', { name: /grid\.monthMakeDecision \(period=April 2026\)/ });
    expect(aprChip.getAttribute('aria-pressed')).toBe('true');
    expect(within(col('MAY')).getByRole('button', { name: /period=May 2026/ }).getAttribute('aria-pressed')).toBe('false');
    expect(within(col('JAN')).queryByRole('button')).toBeNull();
  });

  it('клік по позначці травня робить травень ціллю: кнопка Approve, підсвітка й діалог — травень', async () => {
    show(statuses, { extra: <Headers statuses={statuses} /> });
    await approveButton('April 2026');

    fireEvent.click(await within(col('MAY')).findByRole('button', { name: /period=May 2026/ }));

    const approve = await approveButton('May 2026');
    expect(approve.getAttribute('data-period-key')).toBe(String(May));
    await waitFor(() => expect(col('MAY').className).toContain('ecr-header-decision-target'));
    expect(col('APR').className).not.toContain('ecr-header-decision-target');

    fireEvent.click(approve);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.approvePeriodTitle \(period=May 2026\)/)).toBeTruthy();
  });

  it('клавіатура: Enter і Space на сфокусованій позначці теж задають ціль', async () => {
    const user = userEvent.setup();
    show(statuses, { extra: <Headers statuses={statuses} /> });
    await approveButton('April 2026');

    // Enter → травень.
    const may = await within(col('MAY')).findByRole('button', { name: /period=May 2026/ });
    may.focus();
    await user.keyboard('{Enter}');
    expect((await approveButton('May 2026')).getAttribute('data-period-key')).toBe(String(May));

    // Space → назад на квітень.
    const apr = within(col('APR')).getByRole('button', { name: /period=April 2026/ });
    apr.focus();
    expect(document.activeElement).toBe(apr);
    await user.keyboard(' ');
    expect((await approveButton('April 2026')).getAttribute('data-period-key')).toBe(String(Apr));
  });
});
