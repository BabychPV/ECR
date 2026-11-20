import { createElement, type JSX, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';
import { gridColumns } from '@/features/grid/DocumentGrid';
import { NoLocalFlags } from '@/features/grid/cellState';
import { pickDecisionMonth, useDecisionView } from '@/features/workflow/decisionTarget';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import type { DocumentLock } from '../documentLock';

/**
 * D-6: документ з розкладкою «рік» у місячному проєкті — одна сітка на 12 місяців, стан погодження й ПРАВА в кожного
 * місяця свої; сторінка стоїть на якорі (січень). Approve/Reject/Return for edits беруть ЦІЛЬОВИЙ місяць
 * (`submission-target.monthColumns`: стан + `canDecide`/`canRecall`/`canReturn` цієї людини), як подання — свій (D-441):
 * кнопка називає місяць, ▾ перелічує місяці, над якими людина МОЖЕ діяти, посилання «Review» черги (`decideMonth`) і
 * позначка стану місяця в заголовку колонки задають ціль; «Recall: <місяць>» — пункт на кожен місяць, який можна
 * відкликати; банер каже про місяць.
 *
 * ⛔ На старому коді кнопки дивилися лише на стан якоря: квітень поданий, січень — чернетка → «Approve» не було взагалі.
 * Після першого фіксу (рецензія): право питалося лише для обраного місяця — квітень (крок не мій) ставав ціллю й кнопок
 * не було, травень (мій) був недосяжний; «Recall» ішов за ціллю «Approve»; до відповіді — «Approve» за якорем.
 * Мутації (звіт D-6): ігнорувати `canDecide` у `decideOptions` — «права різні по місяцях» червоні; `recall` за ціллю
 * Approve замість `recallMonths` — «відкликання по місяцях» червоні; прибрати `yearPending` — «поки місяців немає» червоні.
 */

const Jan = 202601;
const Feb = 202602;
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

/** Стан місяця і права ЦІЄЇ людини; рядок — лише стан (поданий — `canDecide`, затверджений — `canReturn`). */
interface MonthSpec {
  readonly status: string;
  readonly decide?: boolean;
  readonly recall?: boolean;
  readonly ret?: boolean;
  /** Місяць джерела HSE400, ще не затверджений (затвердження цього місяця сервер відхилить). */
  readonly source?: number;
}

type Months = Record<number, string | MonthSpec>;

const specOf = (value: string | MonthSpec | undefined): MonthSpec =>
  value === undefined
    ? { status: 'Draft' }
    : typeof value === 'string'
      ? { status: value, decide: value === 'Submitted', ret: value === 'Approved' }
      : value;

interface Post {
  readonly url: string;
  readonly body: { periodKey?: number; approved?: boolean };
}

let posts: Post[] = [];
let gets: string[] = [];

interface MockOptions {
  /** Відповідь `submission-target` чекає на цей проміс (стан «ще вантажиться»). */
  readonly hold?: Promise<void>;
  /** Тримати лише запити цього аркуша (інші відповідають одразу). */
  readonly holdSheet?: number;
  /** `submission-target` відмовляє (500). */
  readonly failTarget?: boolean;
  /** Ціль подання й відкриті періоди відповіді. */
  readonly submit?: { readonly targetPeriodKey: number | null; readonly periods: readonly unknown[] };
  /** Статус відповіді на POST (`409` — стан змінився). */
  readonly postStatus?: number;
  /** Журнал переходів за період. */
  readonly history?: Record<number, readonly unknown[]>;
  /** Відповідь `GET …/approver-edit` (якірний режим). */
  readonly approverEdit?: boolean;
  readonly user?: typeof CurrentUser;
}

/** `months`: стан (і права) за номером місяця; решта (1..6) — `Draft`. */
function mockFetch(months: Months, options: MockOptions = {}): void {
  posts = [];
  gets = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      gets.push(url);

      if (url.includes('/approver-edit')) return json({ canApproverEdit: options.approverEdit ?? true });
      if (url.includes('/api/v1/me')) return json(options.user ?? CurrentUser);

      if (url.includes('/submission-target')) {
        if (options.hold !== undefined && (options.holdSheet === undefined || url.includes(`sheetDefId=${String(options.holdSheet)}`))) {
          await options.hold;
        }
        if (options.failTarget === true) return json({ status: 500, title: 'boom' }, 500);

        return json({
          targetPeriodKey: options.submit?.targetPeriodKey ?? null,
          nextPeriodKey: null,
          periods: options.submit?.periods ?? [],
          monthColumns: Object.entries(Codes).map(([month, code]) => {
            const spec = specOf(months[Number(month)]);

            return {
              tableDefId: Table,
              columnCode: code,
              monthNumber: Number(month),
              periodKey: 202600 + Number(month),
              sheetStatus: spec.status,
              canDecide: spec.decide === true,
              canRecall: spec.recall === true,
              canReturn: spec.ret === true,
              unapprovedSourcePeriodKey: spec.source ?? null,
            };
          }),
        });
      }

      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) as Post['body'] });
        if (options.postStatus !== undefined) {
          return json(
            { status: options.postStatus, title: 'Conflict', errorCode: 'ECR-DOC-0409', detail: 'state changed' },
            options.postStatus,
          );
        }

        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) {
        const period = Number(/periodKey=(\d+)/.exec(url)?.[1]);

        return json(options.history?.[period] ?? []);
      }

      return json(null);
    }),
  );
}

interface ShowOptions extends MockOptions {
  readonly state?: string;
  readonly lock?: DocumentLock | null;
  readonly yearLayout?: boolean;
  readonly decideMonth?: number | null;
  readonly onDecideConsumed?: () => void;
  readonly recalculatedAfterSubmission?: boolean;
  readonly extra?: JSX.Element;
}

interface Shown {
  readonly client: QueryClient;
  readonly rerender: (sheetDefId: number) => void;
}

function show(months: Months, options: ShowOptions = {}): Shown {
  mockFetch(months, options);
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

  const tree = (sheetDefId: number): JSX.Element => (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentActionBar
          documentId={1}
          periodKey={Jan}
          sheetDefId={sheetDefId}
          sheetCode="S1"
          sheetName="Water"
          state={state}
          lock={options.lock ?? null}
          readOnly={false}
          language="en"
          canImport={false}
          canExport={false}
          validate={{ loading: false, run: () => undefined }}
          documentItems={[]}
          yearLayout={options.yearLayout ?? true}
          decideMonth={options.decideMonth ?? null}
          recalculatedAfterSubmission={options.recalculatedAfterSubmission ?? false}
          {...(options.onDecideConsumed === undefined ? {} : { onDecideConsumed: options.onDecideConsumed })}
        />
        {options.extra}
      </QueryClientProvider>
    </MantineProvider>
  );

  const view = render(tree(42));

  return { client, rerender: (sheetDefId) => view.rerender(tree(sheetDefId)) };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const approveButton = (month: string): Promise<HTMLElement> =>
  screen.findByRole('button', { name: new RegExp(`workflow\\.approvePeriod \\(period=${month}\\)`) });

const approvePosts = (): Post[] => posts.filter((p) => p.url.endsWith('/approve'));
const targetGets = (): number => gets.filter((u) => u.includes('/submission-target')).length;

async function openMore(): Promise<void> {
  fireEvent.click(await screen.findByTestId('document-more'));
}

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
    // Право — з `monthColumns` (сервер): окремого запиту `approver-edit` річна розкладка не робить; вплив — за ціллю.
    expect(gets.some((u) => u.includes('/approver-edit'))).toBe(false);
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
});

describe('права різні по місяцях (рецензія №1)', () => {
  it('сценарій A: квітень — крок не мій, травень — мій → ціль травень, квітня немає ні в ▾, ні серед кнопок-позначок', async () => {
    show({ 4: { status: 'Submitted', decide: false }, 5: { status: 'Submitted', decide: true } });

    const approve = await approveButton('May 2026');
    expect(approve.getAttribute('data-period-key')).toBe(String(May));
    expect(screen.getByRole('button', { name: /workflow\.rejectPeriod \(period=May 2026\)/ })).toBeTruthy();
    // Вирішувати можна лише травень — вибирати нема з чого.
    expect(screen.queryByRole('button', { name: '⟦workflow.pickApprovePeriod⟧' })).toBeNull();
    expect(screen.queryByRole('button', { name: /period=April 2026/ })).toBeNull();
  });

  it('сценарій B: ▾ перелічує лише місяці, які людина може вирішувати; `decide` на чужий місяць ігнорується', async () => {
    show(
      {
        3: { status: 'Submitted', decide: true },
        4: { status: 'Submitted', decide: false },
        5: { status: 'Submitted', decide: true },
      },
      { decideMonth: Apr },
    );

    // Квітень із посилання не мій — ціль найраніший мій (березень), а не «нічого».
    expect((await approveButton('March 2026')).getAttribute('data-period-key')).toBe(String(Mar));
    fireEvent.click(await screen.findByRole('button', { name: '⟦workflow.pickApprovePeriod⟧' }));
    await waitFor(() => expect(document.querySelector(`[data-decision-period="${String(May)}"]`)).not.toBeNull());
    expect(document.querySelector(`[data-decision-period="${String(Mar)}"]`)).not.toBeNull();
    expect(document.querySelector(`[data-decision-period="${String(Apr)}"]`)).toBeNull();
  });

  it('жодного місяця, який людина може вирішувати, — ні Approve, ні Reject, банер — «чекає рішення»', async () => {
    show({ 4: { status: 'Submitted', decide: false } });

    const banner = await screen.findByTestId('document-sheet-banner');
    expect(banner.getAttribute('data-banner-period')).toBe(String(Apr));
    expect(banner.textContent).toContain('document.banner.submittedWait');
    expect(screen.queryByRole('button', { name: /workflow\.approve/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /workflow\.reject/ })).toBeNull();
  });

  it('«Return for edits» — лише затверджені місяці з `canReturn`', async () => {
    show({ 2: { status: 'Approved', ret: false }, 3: { status: 'Approved', ret: true } });

    const reopen = await screen.findByRole('button', { name: /workflow\.reopenPeriod \(period=March 2026\)/ });
    expect(reopen.getAttribute('data-period-key')).toBe(String(Mar));
    expect(screen.queryByRole('button', { name: '⟦workflow.pickReopenPeriod⟧' })).toBeNull();
  });
});

describe('відкликання по місяцях (рецензія №4)', () => {
  it('автор без права погодження: «Recall: <місяць>» на КОЖЕН місяць, який можна відкликати; запит — за ним', async () => {
    show({
      4: { status: 'Submitted', recall: true },
      5: { status: 'Submitted', recall: true },
      6: { status: 'Submitted', recall: false },
    });

    // Ні Approve, ні Reject: автор не погоджує.
    await waitFor(() => expect(targetGets()).toBeGreaterThan(0));
    await openMore();
    const items = await screen.findAllByTestId('document-recall');
    expect(items.map((item) => item.getAttribute('data-period-key'))).toEqual([String(Apr), String(May)]);
    expect(items[1]?.textContent).toContain('workflow.recallPeriod (period=May 2026)');
    expect(screen.queryByRole('button', { name: /workflow\.approve/ })).toBeNull();

    fireEvent.click(items[1] as HTMLElement);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.recallPeriodTitle \(period=May 2026\)/)).toBeTruthy();
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'typo' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.recall⟧$/ }));

    await waitFor(() => expect(posts.filter((p) => p.url.endsWith('/recall'))).toHaveLength(1));
    expect(posts.find((p) => p.url.endsWith('/recall'))?.body).toMatchObject({ periodKey: May });
    // Річна розкладка не питає `GET …/recall` — право вже в місяцях.
    expect(gets.some((u) => u.includes('/recall?'))).toBe(false);
  });

  it('відкликання не залежить від цілі Approve: найраніший поданий не мій для відкликання — травень однаково є', async () => {
    show({
      4: { status: 'Submitted', decide: true, recall: false },
      5: { status: 'Submitted', decide: false, recall: true },
    });

    expect(await approveButton('April 2026')).toBeTruthy();
    await openMore();
    const items = await screen.findAllByTestId('document-recall');
    expect(items.map((item) => item.getAttribute('data-period-key'))).toEqual([String(May)]);
  });
});

describe('поки місяців немає — дій рішення немає (рецензія №5)', () => {
  it('відповідь `submission-target` у дорозі: ні «Approve», ні «Approve: …»; скелет; потім — квітень', async () => {
    let release = (): void => undefined;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    // Якір теж поданий: старий код показав би «Approve» за січнем.
    show({ 1: 'Submitted', 4: 'Submitted' }, { state: 'Submitted', hold });

    await screen.findByTestId('document-actions-pending');
    await waitFor(() => expect(gets.some((u) => u.includes('/api/v1/me'))).toBe(true));
    expect(screen.queryByRole('button', { name: /workflow\.approve/ })).toBeNull();

    await act(async () => {
      release();
      await Promise.resolve();
    });

    expect((await approveButton('January 2026')).getAttribute('data-period-key')).toBe(String(Jan));
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();
  });

  it('інший аркуш, його відповідь у дорозі (підставні дані попереднього): дій рішення немає зовсім', async () => {
    let release = (): void => undefined;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const shown = show({ 1: 'Submitted', 4: 'Submitted' }, { state: 'Submitted', hold, holdSheet: 43 });
    await approveButton('January 2026');

    shown.rerender(43);
    // Квітень попереднього аркуша (підставна відповідь) і «Approve» за якорем — обидва були б неправдою.
    await waitFor(() => expect(screen.queryByRole('button', { name: /workflow\.approve/ })).toBeNull());
    expect(screen.queryByTestId('document-actions-pending')).toBeNull();

    await act(async () => {
      release();
      await Promise.resolve();
    });
    expect(await approveButton('January 2026')).toBeTruthy();
  });

  it('збій `submission-target` — стара поведінка за якорем («Approve» без місяця)', async () => {
    show({}, { state: 'Submitted', failTarget: true });

    const approve = await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    expect(approve.getAttribute('data-period-key')).toBeNull();
  });
});

describe('банер річної розкладки (рецензія №3)', () => {
  it('поданий квітень: банер про квітень і його колонку, не «аркуш подано, правка закрита»', async () => {
    show({ 4: 'Submitted' });

    const banner = await screen.findByTestId('document-sheet-banner');
    expect(banner.getAttribute('data-banner-period')).toBe(String(Apr));
    expect(banner.textContent).toContain('document.banner.submitted');
    expect(banner.textContent).toContain('April 2026');
    expect(banner.textContent).toContain('document.banner.monthSubmitted (period=April 2026)');
    expect(banner.textContent).not.toContain('grid.submittedReadOnlyHint');
    expect(banner.textContent).toContain('document.banner.submittedDecide');
  });

  it('затверджений березень: «March 2026 is approved; its column is read-only»', async () => {
    show({ 3: 'Approved' });

    const banner = await screen.findByTestId('document-sheet-banner');
    expect(banner.textContent).toContain('document.banner.monthApproved (period=March 2026)');
    expect(banner.textContent).not.toContain('document.lock.sheetApproved');
  });

  it('відхилений якір (січень) лишається з причиною поруч із банером поданого квітня', async () => {
    show(
      { 1: 'Rejected', 4: 'Submitted' },
      {
        state: 'Rejected',
        history: {
          [Jan]: [
            {
              action: 'Reject',
              approvedBySubmitter: false,
              at: '2026-02-03T10:00:00Z',
              byDisplayName: 'D. Akhmetova',
              fromState: 'Submitted',
              toState: 'Rejected',
              periodKey: Jan,
              reason: 'wrong meter',
              sheetCode: 'S1',
              stepOrdinal: null,
            },
          ],
        },
      },
    );

    await waitFor(() => expect(screen.getAllByTestId('document-sheet-banner')).toHaveLength(2));
    const [rejected, month] = screen.getAllByTestId('document-sheet-banner');
    expect(rejected?.getAttribute('data-banner-period')).toBe(String(Jan));
    await waitFor(() => expect(rejected?.textContent).toContain('wrong meter'));
    expect(month?.getAttribute('data-banner-period')).toBe(String(Apr));
  });

  it('«перераховано після подання» (позначка якоря) — лише коли банер про якір', async () => {
    show({ 4: 'Submitted' }, { recalculatedAfterSubmission: true });
    const banner = await screen.findByTestId('document-sheet-banner');
    expect(banner.getAttribute('data-banner-period')).toBe(String(Apr));
    expect(banner.querySelector('[data-recalculated-after-submission]')).toBeNull();
  });

  it('«перераховано після подання» — є, коли поданий сам якір', async () => {
    show({ 1: 'Submitted' }, { state: 'Submitted', recalculatedAfterSubmission: true });
    const banner = await screen.findByTestId('document-sheet-banner');
    await waitFor(() => expect(banner.getAttribute('data-banner-period')).toBe(String(Jan)));
    expect(banner.querySelector('[data-recalculated-after-submission]')).not.toBeNull();
  });

  it('ширша причина (період закрито) важливіша за стан місяця', async () => {
    show({ 4: 'Submitted' }, { lock: 'periodClosed' });
    await approveButton('April 2026');

    expect(screen.queryByTestId('document-sheet-banner')).toBeNull();
    expect(document.querySelector('[data-document-lock="periodClosed"]')).not.toBeNull();
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

describe('`?decide` діє один раз (рецензія №9)', () => {
  it('щойно місяці відомі — сторінка прибирає параметр, а ціль лишається травнем', async () => {
    const consumed = vi.fn();
    show({ 4: 'Submitted', 5: 'Submitted' }, { decideMonth: May, onDecideConsumed: consumed });

    expect((await approveButton('May 2026')).getAttribute('data-period-key')).toBe(String(May));
    await waitFor(() => expect(consumed).toHaveBeenCalledTimes(1));
  });

  it('`decide` на чужий місяць теж прибирається', async () => {
    const consumed = vi.fn();
    show({ 4: 'Submitted' }, { decideMonth: 202606, onDecideConsumed: consumed });

    await approveButton('April 2026');
    await waitFor(() => expect(consumed).toHaveBeenCalledTimes(1));
  });

  it('зміна аркуша до застосування — параметр прибирається', async () => {
    let release = (): void => undefined;
    const hold = new Promise<void>((resolve) => {
      release = resolve;
    });
    const consumed = vi.fn();
    const shown = show({ 4: 'Submitted' }, { decideMonth: Apr, onDecideConsumed: consumed, hold });
    await screen.findByTestId('document-actions-pending');
    expect(consumed).not.toHaveBeenCalled();

    shown.rerender(43);
    await waitFor(() => expect(consumed).toHaveBeenCalled());
    release();
  });
});

describe('відмова 409 — стани місяців перечитуються (рецензія №10)', () => {
  it('Approve: 409 → `submission-target` запитується знову, відмова показана', async () => {
    show({ 4: 'Submitted' }, { postStatus: 409 });

    fireEvent.click(await approveButton('April 2026'));
    const dialog = await screen.findByRole('dialog');
    const before = targetGets();
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.approve⟧$/ }));

    await waitFor(() => expect(approvePosts()).toHaveLength(1));
    await waitFor(() => expect(targetGets()).toBeGreaterThan(before));
  });

  it('Recall: 409 → `submission-target` запитується знову', async () => {
    show({ 4: { status: 'Submitted', recall: true } }, { postStatus: 409 });

    await waitFor(() => expect(targetGets()).toBeGreaterThan(0));
    await openMore();
    fireEvent.click((await screen.findAllByTestId('document-recall'))[0] as HTMLElement);
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'typo' } });
    const before = targetGets();
    fireEvent.click(within(dialog).getByRole('button', { name: /^⟦workflow\.recall⟧$/ }));

    await waitFor(() => expect(posts.filter((p) => p.url.endsWith('/recall'))).toHaveLength(1));
    await waitFor(() => expect(targetGets()).toBeGreaterThan(before));
  });
});

describe('«Submit» і місяць джерела HSE400 (рецензія №11)', () => {
  const Submitter = { ...CurrentUser, grants: { 'Project:7': 'Submit' }, permissions: ['Document.View'] };

  it('місяць джерела для ЦІЛІ подання не затверджено — «Submit» недоступна з поясненням, клік нічого не відкриває', async () => {
    show(
      {},
      {
        user: Submitter,
        yearLayout: false,
        submit: {
          targetPeriodKey: Feb,
          periods: [
            { periodKey: Jan, state: 'Open', sheetStatus: 'Draft', submittable: false, unapprovedSourcePeriodKey: null },
            { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true, unapprovedSourcePeriodKey: Feb },
          ],
        },
      },
    );

    const submit = await screen.findByTestId('document-submit');
    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Feb)));
    expect(submit.getAttribute('aria-disabled')).toBe('true');
    expect(submit.getAttribute('data-blocked')).toBe('true');
    // Опис для читача — причина (підказка `Hint` описує кнопку завжди).
    const describedBy = submit.getAttribute('aria-describedby') ?? '';
    expect(document.getElementById(describedBy)?.textContent).toContain(
      'workflow.submitSourceNotApproved (period=February 2026, source=February 2026)',
    );

    fireEvent.click(submit);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('незатверджене джерело ІНШОГО періоду (не цілі) подання не блокує', async () => {
    show(
      {},
      {
        user: Submitter,
        yearLayout: false,
        submit: {
          targetPeriodKey: Feb,
          periods: [
            { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true, unapprovedSourcePeriodKey: null },
            { periodKey: Mar, state: 'Open', sheetStatus: 'Draft', submittable: true, unapprovedSourcePeriodKey: Mar },
          ],
        },
      },
    );

    const submit = await screen.findByTestId('document-submit');
    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Feb)));
    expect(submit.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(submit);
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });
});

describe('«Approve» і місяць джерела HSE400 (борг координатора)', () => {
  const describedText = (element: HTMLElement): string =>
    document.getElementById(element.getAttribute('aria-describedby') ?? '')?.textContent ?? '';

  it('річний документ: джерело місяця-цілі не затверджено — «Approve» недоступна з поясненням, «Reject» доступна', async () => {
    show({ 4: { status: 'Submitted', decide: true, source: Apr }, 5: 'Submitted' });

    const approve = await approveButton('April 2026');
    await waitFor(() => expect(approve.getAttribute('aria-disabled')).toBe('true'));
    expect(approve.getAttribute('data-blocked')).toBe('true');
    expect(describedText(approve)).toContain('workflow.approveSourceNotApproved (period=April 2026, source=April 2026)');

    fireEvent.click(approve);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByRole('dialog')).toBeNull();

    // Відхилення ворота не стосуються.
    const reject = screen.getByRole('button', { name: /workflow\.rejectPeriod \(period=April 2026\)/ });
    expect(reject.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(reject);
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });

  it('річний документ: інший місяць-ціль (джерело затверджене) — «Approve» знову доступна', async () => {
    show({ 4: { status: 'Submitted', decide: true, source: Apr }, 5: 'Submitted' });

    await approveButton('April 2026');
    fireEvent.click(await screen.findByRole('button', { name: '⟦workflow.pickApprovePeriod⟧' }));
    const mayItem = await waitFor(() => {
      const item = document.querySelector<HTMLElement>(`[data-decision-period="${String(May)}"]`);
      expect(item).not.toBeNull();

      return item as HTMLElement;
    });
    fireEvent.click(mayItem);

    const may = await approveButton('May 2026');
    expect(may.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(may);
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });

  it('звичайний документ: джерело періоду сторінки не затверджено — «Approve» недоступна', async () => {
    show(
      {},
      {
        state: 'Submitted',
        yearLayout: false,
        submit: {
          targetPeriodKey: null,
          periods: [{ periodKey: Jan, state: 'Open', sheetStatus: 'Submitted', submittable: false, unapprovedSourcePeriodKey: Jan }],
        },
      },
    );

    const approve = await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    await waitFor(() => expect(approve.getAttribute('aria-disabled')).toBe('true'));
    expect(describedText(approve)).toContain('workflow.approveSourceNotApproved (period=January 2026, source=January 2026)');
    expect(screen.getByRole('button', { name: '⟦workflow.reject⟧' }).getAttribute('aria-disabled')).toBeNull();
  });

  it('звичайний документ: незатверджене джерело ІНШОГО періоду затвердження не блокує', async () => {
    show(
      {},
      {
        state: 'Submitted',
        yearLayout: false,
        submit: {
          targetPeriodKey: Feb,
          periods: [
            { periodKey: Jan, state: 'Open', sheetStatus: 'Submitted', submittable: false, unapprovedSourcePeriodKey: null },
            { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true, unapprovedSourcePeriodKey: Feb },
          ],
        },
      },
    );

    const approve = await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    await waitFor(() => expect(gets.some((u) => u.includes('/submission-target'))).toBe(true));
    expect(approve.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(approve);
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });
});

describe('звичайний документ без розкладки «рік» — без змін', () => {
  it('поданий якір: «Approve» без місяця, без ▾, запит — за періодом сторінки', async () => {
    show({ 4: 'Submitted' }, { state: 'Submitted', lock: 'sheetSubmitted', yearLayout: false });

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
    // Банер — як був: речення посібника про аркуш.
    expect((await screen.findByTestId('document-sheet-banner')).textContent).toContain('grid.submittedReadOnlyHint');
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
function Headers({ months }: { readonly months: Months }): JSX.Element {
  const view = useDecisionView(1, 42);
  const marked = new Map<string, 'Submitted' | 'Approved'>();
  const periodOf = new Map<string, { periodKey: number }>();
  for (const [month, code] of Object.entries(Codes)) {
    periodOf.set(code, { periodKey: 202600 + Number(month) });
    const status = specOf(months[Number(month)]).status;
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
  const months: Months = { 4: 'Submitted', 5: 'Submitted' };
  const col = (code: string): HTMLElement => document.querySelector<HTMLElement>(`[data-col="${code}"]`) as HTMLElement;
  const chip = (code: string, month: string): HTMLElement =>
    within(col(code)).getByRole('button', { name: new RegExp(`period=${month}`) });

  it('колонка-ціль підсвічена; позначка доступного місяця — кнопка з назвою; чернетка — без кнопки', async () => {
    show(months, { extra: <Headers months={months} /> });
    await approveButton('April 2026');

    await waitFor(() => expect(col('APR').className).toContain('ecr-header-decision-target'));
    expect(col('APR').getAttribute('data-decision-target')).toBe('decide');
    expect(col('MAY').className).not.toContain('ecr-header-decision-target');
    expect(col('JAN').className).not.toContain('ecr-header-decision-target');

    // Кнопка з доступною назвою — на поданих місяцях; на січні (чернетка) позначки немає зовсім.
    expect(chip('APR', 'April 2026').getAttribute('aria-pressed')).toBe('true');
    expect(chip('MAY', 'May 2026').getAttribute('aria-pressed')).toBe('false');
    expect(within(col('JAN')).queryByRole('button')).toBeNull();
  });

  it('доступна назва позначки починається з видимого стану (WCAG 2.5.3)', async () => {
    show(months, { extra: <Headers months={months} /> });
    await approveButton('April 2026');

    const apr = await waitFor(() => chip('APR', 'April 2026'));
    const visible = apr.textContent ?? '';
    expect(visible.length).toBeGreaterThan(0);
    const name = apr.getAttribute('aria-label') ?? '';
    expect(name).toContain('grid.monthMakeDecision');
    expect(name).toContain(`status=${visible}`);
  });

  it('місяць, який людина не може вирішувати, — текст, не кнопка', async () => {
    const mixed: Months = { 4: { status: 'Submitted', decide: false }, 5: 'Submitted' };
    show(mixed, { extra: <Headers months={mixed} /> });
    await approveButton('May 2026');

    await waitFor(() => expect(chip('MAY', 'May 2026').getAttribute('aria-pressed')).toBe('true'));
    expect(within(col('APR')).queryByRole('button')).toBeNull();
    expect(col('APR').querySelector('[data-month-status="Submitted"]')).not.toBeNull();
  });

  it('клік по позначці травня робить травень ціллю: кнопка Approve, підсвітка й діалог — травень', async () => {
    show(months, { extra: <Headers months={months} /> });
    await approveButton('April 2026');

    fireEvent.click(await waitFor(() => chip('MAY', 'May 2026')));

    const approve = await approveButton('May 2026');
    expect(approve.getAttribute('data-period-key')).toBe(String(May));
    await waitFor(() => expect(col('MAY').className).toContain('ecr-header-decision-target'));
    expect(col('APR').className).not.toContain('ecr-header-decision-target');

    fireEvent.click(approve);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/workflow\.approvePeriodTitle \(period=May 2026\)/)).toBeTruthy();
  });

  it('клавіатура: Enter і Space на сфокусованій позначці теж задають ціль; фокус лишається на ТІЙ САМІЙ позначці', async () => {
    const user = userEvent.setup();
    show(months, { extra: <Headers months={months} /> });
    await approveButton('April 2026');

    // Enter → травень; фокус — на тому самому елементі (без миготіння кнопка → текст → кнопка).
    const may = await waitFor(() => chip('MAY', 'May 2026'));
    may.focus();
    await user.keyboard('{Enter}');
    expect((await approveButton('May 2026')).getAttribute('data-period-key')).toBe(String(May));
    await waitFor(() => expect(may.getAttribute('aria-pressed')).toBe('true'));
    expect(may.isConnected).toBe(true);
    expect(document.activeElement).toBe(may);

    // Space → назад на квітень.
    const apr = chip('APR', 'April 2026');
    apr.focus();
    expect(document.activeElement).toBe(apr);
    await user.keyboard(' ');
    expect((await approveButton('April 2026')).getAttribute('data-period-key')).toBe(String(Apr));
    await waitFor(() => expect(apr.getAttribute('aria-pressed')).toBe('true'));
    expect(document.activeElement).toBe(apr);
  });

  it('затверджений місяць: позначка задає ціль «Return for edits» — свій вигляд і `aria-pressed` (рецензія №7)', async () => {
    const approved: Months = { 3: 'Approved', 5: 'Approved' };
    show(approved, { extra: <Headers months={approved} /> });
    await screen.findByRole('button', { name: /workflow\.reopenPeriod \(period=March 2026\)/ });

    await waitFor(() => expect(col('MAR').className).toContain('ecr-header-return-target'));
    expect(col('MAR').getAttribute('data-decision-target')).toBe('return');
    const mayChip = within(col('MAY')).getByRole('button', { name: /grid\.monthMakeReturn .*period=May 2026/ });
    expect(mayChip.getAttribute('aria-pressed')).toBe('false');

    fireEvent.click(mayChip);
    expect(
      (await screen.findByRole('button', { name: /workflow\.reopenPeriod \(period=May 2026\)/ })).getAttribute('data-period-key'),
    ).toBe(String(May));
    await waitFor(() => expect(mayChip.getAttribute('aria-pressed')).toBe('true'));
    expect(col('MAY').className).toContain('ecr-header-return-target');
    expect(col('MAR').className).not.toContain('ecr-header-return-target');
  });
});
