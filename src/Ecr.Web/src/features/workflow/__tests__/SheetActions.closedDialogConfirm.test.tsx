import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { settleAndFlushEdits } from '@/features/grid/settleEdits';
import { StubReason } from '@/test/alwaysMountedDialogs';
import { LazyConfirmModal, LazyReasonModal } from '../lazyDialogs';
import { SheetActions } from '../SheetActions';

/**
 * B145: діалог підтвердження, що вже ЗАКРИВАЄТЬСЯ, не виконує дію вдруге.
 *
 * ⛔ Дефект продукту. Після відповіді сервера діалог закривається (`opened = false`), а власник знімає
 * зайнятість (`useSettledAction`, `isPending`) — але `Modal` ще ~200 мс лишається в DOM на анімацію
 * закриття, і його кнопка підтвердження далі викликала `onConfirm`. Зонд (миттєва відповідь сервера,
 * клік щораз 50 мс по кнопці діалогу, що закривається): 5 `POST …/submit` замість одного. Людині для
 * цього досить подвійного кліку, повільнішого за відповідь сервера. Захист — в обгортках
 * `lazyDialogs.tsx` (один на всі діалоги, що через них ідуть).
 *
 * ⚠ Без жодної залежності від часу: діалоги підмінено кнопками, які є в DOM завжди
 * (`@/test/alwaysMountedDialogs`), а відповідь сервера віддає сам тест (`respond`).
 *
 * Мутаційний доказ: прибрати `props.opened ? … : ignore` в `LazyConfirmModal` — червоні Submit, Approve і
 * тест обгорток; у `LazyReasonModal` — Reject, Reopen, Recall і тест обгорток.
 */

vi.mock('@/shared/ui/ConfirmModal', async () => ({
  ConfirmModal: (await import('@/test/alwaysMountedDialogs')).ConfirmModal,
}));
vi.mock('@/shared/ui/ReasonModal', async () => ({
  ReasonModal: (await import('@/test/alwaysMountedDialogs')).ReasonModal,
}));

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Manage' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View', 'Document.Reopen'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

/** Запити запису, що дійшли до «сервера», у порядку надходження: `POST submit {…тіло…}`. */
const log: string[] = [];
/** Відпускає відповідь на останній запит запису; `null` — запиту ще не було. */
let respond: (() => void) | null = null;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'POST') {
        const action = (url.split('?')[0] ?? '').split('/').at(-1) ?? '';
        log.push(`POST ${action} ${String(init.body)}`);
        // Сервер відповідає тоді, коли скаже тест.
        await new Promise<void>((resolve) => {
          respond = resolve;
        });

        return json({});
      }

      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/submission-target')) {
        return json({
          targetPeriodKey: 202601,
          nextPeriodKey: null,
          periods: [{ periodKey: 202601, state: 'Open', sheetStatus: 'Draft', submittable: true }],
          monthColumns: [],
        });
      }
      if (url.includes('/approver-edit?')) return json({ canApproverEdit: true });
      if (url.includes('/recall?')) return json({ canRecall: true });

      return json({});
    }),
  );
}

function show(state: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: {},
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={2} periodKey={202601} state={state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  log.length = 0;
  respond = null;
  vi.unstubAllGlobals();
});

interface Case {
  /** Стан аркуша, у якому дія доступна. */
  readonly state: string;
  /** Кнопка рядка дій, що відкриває діалог. */
  readonly opener: string | RegExp;
  /** Кнопка підтвердження в діалозі (заглушка). */
  readonly confirm: string;
  /** Єдиний запит, який має піти. */
  readonly sent: string;
}

const Address = '"sheetDefId":2,"periodKey":202601';

const Cases: Readonly<Record<string, Case>> = {
  Submit: {
    state: 'Draft',
    opener: /workflow\.submitPeriod/,
    confirm: 'confirm:⟦document.submit⟧',
    sent: `POST submit {${Address},"acknowledgeWarnings":false}`,
  },
  Approve: {
    state: 'Submitted',
    opener: '⟦workflow.approve⟧',
    confirm: 'confirm:⟦workflow.approve⟧',
    sent: `POST approve {${Address},"approved":true,"reason":null}`,
  },
  Reject: {
    state: 'Submitted',
    opener: '⟦workflow.reject⟧',
    confirm: 'reason:⟦workflow.reject⟧',
    sent: `POST approve {${Address},"approved":false,"reason":"${StubReason}"}`,
  },
  Reopen: {
    state: 'Submitted',
    opener: '⟦workflow.reopen⟧',
    confirm: 'reason:⟦workflow.reopen⟧',
    sent: `POST reopen {${Address},"reason":"${StubReason}"}`,
  },
  Recall: {
    state: 'Submitted',
    opener: '⟦workflow.recall⟧',
    confirm: 'reason:⟦workflow.recall⟧',
    sent: `POST recall {${Address},"reason":"${StubReason}"}`,
  },
};

describe('B145: підтвердження діалогу, що закривається, не виконує дію вдруге', () => {
  it.each(Object.keys(Cases))('%s: клік по кнопці діалогу після відповіді сервера не дає другого запиту', async (name) => {
    const { state, opener, confirm: confirmName, sent } = Cases[name] as Case;
    mockFetch();
    show(state);

    fireEvent.click(await screen.findByRole('button', { name: opener }));
    const confirm = await screen.findByRole('button', { name: confirmName });
    expect(confirm.getAttribute('data-opened')).toBe('true');

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(log).toEqual([sent]);
    });

    // Сервер відповів: діалог закрито, дія завершилась (зайнятість знято).
    respond?.();
    await waitFor(() => {
      expect(confirm.getAttribute('data-opened')).toBe('false');
      expect(confirm.getAttribute('data-pending')).toBe('false');
    });

    // Клік по кнопці діалогу, що закривається (у справжньому `Modal` вона ще в DOM на час анімації).
    fireEvent.click(confirm);

    // Той самий кадр + тік, який дія чекає перед запитом (`settleAndFlushEdits`): незахищений запит уже пішов би.
    await settleAndFlushEdits();
    await settleAndFlushEdits();
    expect(log).toEqual([sent]);
    expect(confirm.getAttribute('data-pending')).toBe('false');
  });

  it('обгортки діалогів: відкритий підтверджує, закритий (ще в DOM) — ні', async () => {
    const confirmed = vi.fn();
    const reasoned = vi.fn();
    const dialogs = (opened: boolean): JSX.Element => (
      <>
        <LazyConfirmModal opened={opened} title="t" verb="go" onConfirm={confirmed} onClose={() => undefined} />
        <LazyReasonModal
          opened={opened}
          title="t"
          label="l"
          confirmLabel="go"
          onConfirm={reasoned}
          onClose={() => undefined}
        />
      </>
    );
    const view = render(dialogs(true));

    fireEvent.click(await screen.findByRole('button', { name: 'confirm:go' }));
    fireEvent.click(await screen.findByRole('button', { name: 'reason:go' }));
    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(reasoned).toHaveBeenCalledTimes(1);
    expect(reasoned).toHaveBeenCalledWith(StubReason);

    // Діалог закривається: `opened = false`, кнопка ще на місці.
    view.rerender(dialogs(false));
    fireEvent.click(screen.getByRole('button', { name: 'confirm:go' }));
    fireEvent.click(screen.getByRole('button', { name: 'reason:go' }));
    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(reasoned).toHaveBeenCalledTimes(1);

    // Відкрили знову — підтвердження знову працює (захист не «залипає»).
    view.rerender(dialogs(true));
    fireEvent.click(screen.getByRole('button', { name: 'confirm:go' }));
    fireEvent.click(screen.getByRole('button', { name: 'reason:go' }));
    expect(confirmed).toHaveBeenCalledTimes(2);
    expect(reasoned).toHaveBeenCalledTimes(2);
  });
});
