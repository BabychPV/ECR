import { isValidElement, type ReactElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentLock } from '@/features/documents/documentLock';
import { SheetActions } from '../SheetActions';

/**
 * Дії аркуша, четвертий раунд перевірки живцем:
 *
 *  - `F-17` — оператор із грантом Write не бачив «Submit» і не знав чому;
 *  - `F-18` — у закритому періоді й архівному проєкті «Submit» і
 *    «Recalculate» стояли активні, хоча сервер відмовляв;
 *  - `X-05` — «Approve» у `color="green"` мав контраст 2.4:1;
 *  - `X-25` — «Approve» спрацьовував без підтвердження;
 *  - `X-04` — провал перерахунку показував сирий текст сервера (українською,
 *    текст винятку SQL), а не причину з каталогу.
 */

const SlowEnvTimeout = 400_000;

const DocumentId = 1;
const SheetDefId = 42;
const PeriodKey = 202601;
const ProjectId = 7;

vi.mock('@mantine/notifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/notifications')>();

  return { ...actual, notifications: { ...actual.notifications, show: vi.fn() } };
});

const posts: { url: string; body: unknown }[] = [];

function mockFetch(options: { grants: Record<string, string>; jobState?: string }): void {
  posts.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: options.grants,
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body ?? 'null')) as unknown });

        return url.includes('/recalculate') ? json({ jobId: 'job-1' }, 202) : json({});
      }

      if (url.includes('/api/v1/jobs/')) {
        return json({
          jobId: 'job-1',
          state: options.jobState ?? 'Running',
          percent: 100,
          message: null,
          // ⛔ Саме те, що бачив оператор: речення сервера українською.
          error: 'Не вдалося перерахувати: помилка SQL 547 (FK_CalculationResult_Row).',
          errorCode: 'ECR-CALC-4221',
          correlationId: 'corr-77',
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function show(options: {
  grants: Record<string, string>;
  state: string;
  lock?: DocumentLock | null;
  jobState?: string;
}): void {
  mockFetch(options);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', DocumentId, PeriodKey], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: DocumentId,
    nameL10n: null,
    projectId: ProjectId,
    sheetCount: 1,
    sheetStates: { S1: options.state },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions
          documentId={DocumentId}
          sheetDefId={SheetDefId}
          periodKey={PeriodKey}
          state={options.state}
          lock={options.lock ?? null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

describe('F-17: «Submit» при гранті Write — видно, вимкнено й пояснено', () => {
  it(
    'кнопка є, не подає, і каже, якого рівня бракує',
    async () => {
      show({ grants: { 'Project:7': 'Write' }, state: 'Draft' });

      const submit = await screen.findByTestId('submit-needs-grant', {}, { timeout: SlowEnvTimeout });

      expect(submit.getAttribute('aria-disabled')).toBe('true');

      // ⚠ Пояснення прив'язане до кнопки (`aria-describedby`), а не лише в
      // спливній підказці: читалка чує його разом із назвою кнопки.
      const describedBy = submit.getAttribute('aria-describedby') ?? '';
      expect(describedBy).not.toBe('');

      fireEvent.focus(submit);
      expect(await screen.findByText(/workflow\.submitNeedsGrant.*level=Write/)).toBeTruthy();

      fireEvent.click(submit);
      expect(posts).toHaveLength(0);
    },
    SlowEnvTimeout,
  );

  it(
    'грант Submit — звичайна кнопка, без пояснення',
    async () => {
      show({ grants: { 'Project:7': 'Submit' }, state: 'Draft' });

      await screen.findByRole('button', { name: /document\.submit/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('submit-needs-grant')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'грант Read — подавати не збирався: ні кнопки, ні пояснення',
    async () => {
      show({ grants: { 'Project:7': 'Read' }, state: 'Draft' });

      await screen.findByRole('button', { name: /workflow\.recalculate/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('submit-needs-grant')).toBeNull();
      expect(screen.queryByRole('button', { name: /document\.submit/ })).toBeNull();
    },
    SlowEnvTimeout,
  );
});

describe('F-18: закритий період і архівний проєкт — жодної дії, яку сервер відхилить', () => {
  it.each<DocumentLock>(['periodClosed', 'periodNotOpen', 'projectArchived'])(
    '%s — ні «Submit», ні «Recalculate»',
    async (lock) => {
      show({ grants: { 'Project:7': 'Manage' }, state: 'Draft', lock });

      // ⚠ Профіль доїхав — інакше відсутність кнопок нічого не доводила б.
      await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
      await new Promise((resolve) => setTimeout(resolve, 200));

      expect(screen.queryByRole('button', { name: /document\.submit/ })).toBeNull();
      expect(screen.queryByRole('button', { name: /workflow\.recalculate/ })).toBeNull();
      expect(screen.queryByTestId('submit-needs-grant')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'без причини ті самі кнопки на місці (дзеркало)',
    async () => {
      show({ grants: { 'Project:7': 'Manage' }, state: 'Draft', lock: null });

      expect(await screen.findByRole('button', { name: /document\.submit/ }, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.getByRole('button', { name: /workflow\.recalculate/ })).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});

describe('X-05, X-25: «Approve» — контрастний токен і підтвердження', () => {
  it(
    'клік не затверджує одразу — спершу діалог; затверджує лише підтвердження',
    async () => {
      show({ grants: { 'Project:7': 'Approve' }, state: 'Submitted' });

      const approve = await screen.findByRole('button', { name: /workflow\.approve/ }, { timeout: SlowEnvTimeout });

      // ⛔ `X-05`: не `green` (2.4:1), а `statusSuccess` — токен теми, що
      // тримає AA 4.5:1 (`contrast.test.ts`).
      expect(approve.getAttribute('style') ?? '').toContain('statusSuccess');
      expect(approve.getAttribute('style') ?? '').not.toContain('--mantine-color-green');

      fireEvent.click(approve);
      expect(posts).toHaveLength(0);

      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).toContain('workflow.approveHint');

      const confirm = [...dialog.querySelectorAll('button')].find((button) =>
        (button.textContent ?? '').includes('workflow.approve'),
      );
      if (confirm === undefined) throw new Error('немає кнопки підтвердження');
      fireEvent.click(confirm);

      await waitFor(() => expect(posts).toHaveLength(1));
      expect(posts[0]?.url).toContain('/approve');
      expect(posts[0]?.body).toMatchObject({ approved: true, sheetDefId: SheetDefId, periodKey: PeriodKey });
    },
    SlowEnvTimeout,
  );
});

describe('X-04: провал перерахунку — причина з каталогу, а не сирий текст сервера', () => {
  it(
    'тост несе код через каталог і кореляцію, а не речення сервера',
    async () => {
      show({ grants: { 'Project:7': 'Manage' }, state: 'Draft', jobState: 'Failed' });

      fireEvent.click(await screen.findByRole('button', { name: /workflow\.recalculate/ }, { timeout: SlowEnvTimeout }));

      await waitFor(() =>
        expect(vi.mocked(notifications.show).mock.calls.some(([options]) => options.color === 'statusError')).toBe(true),
      );

      const call = vi.mocked(notifications.show).mock.calls.find(([options]) => options.color === 'statusError');
      const message = call?.[0].message;

      expect(typeof message).not.toBe('string');
      expect(isValidElement(message)).toBe(true);

      const props = (message as ReactElement<{ errorCode?: string; correlationId?: string; state?: string }>).props;
      expect(props.errorCode).toBe('ECR-CALC-4221');
      expect(props.correlationId).toBe('corr-77');
      expect(JSON.stringify(call?.[0])).not.toContain('помилка SQL');
    },
    SlowEnvTimeout,
  );
});
