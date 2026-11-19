import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { components } from '@/api/schema';
import { SheetActions } from '../SheetActions';

/**
 * B80 N5 (D-554): Approve і Reopen над аркушем-джерелом питають вплив на залежні документи. Не потрібен — дія йде як
 * раніше; потрібен — діалог впливу, і дію виконує його підтвердження (відбиток + причина за політикою). Block — без
 * кнопки. Збій запиту впливу дію не блокує: сервер відмовляє 409 — діалог відкривається за відмовою.
 *
 * ⛔ Мутація (прогнано локально): у `guardImpact` прибрати гілку `!impact.required → proceed` (завжди діалог) —
 * червоніє a); прибрати `confirmedImpact` з `impactPart` — червоніють b) і c).
 */

type Impact = components['schemas']['SourceImpactDto'];

const Me = {
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

function impact(overrides: Partial<Impact> = {}): Impact {
  return {
    action: 'approve',
    policy: 'Warn',
    required: true,
    blocked: false,
    reasonRequired: false,
    impactHash: 'hash-1',
    sourceMonths: [202601],
    dependentCount: 1,
    approvedCount: 1,
    consumers: [
      { bindingCode: 'B', templateCode: 'LAND', documentId: 5, projectCode: 'LAND-26', documentKey: 'DOC-LAND', periodKey: 202601, approved: true, willRecalculate: false, skipReason: 'approved' },
    ],
    ...overrides,
  };
}

const posts: { url: string; body: Record<string, unknown> }[] = [];

/**
 * @param impacts відповіді `GET …/source-impact` по черзі (остання повторюється); `'fail'` — запит падає.
 * @param refusals відмови `POST` по черзі (до першого успіху).
 */
function show(options: {
  state: 'Submitted' | 'Approved';
  impacts: (Impact | 'fail')[];
  refusals?: { status: number; errorCode: string; messageKey: string }[];
}): void {
  posts.length = 0;
  let impactCalls = 0;
  const refusals = [...(options.refusals ?? [])];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/source-impact')) {
        const next = options.impacts[Math.min(impactCalls, options.impacts.length - 1)];
        impactCalls += 1;
        if (next === 'fail') return json({ title: 'boom', status: 500, errorCode: 'ECR-SYS-0500' }, 500);

        return json(next);
      }
      if (url.includes('/approver-edit')) return json({ canApproverEdit: true });
      if (url.includes('/api/v1/me')) return json(Me);
      if (url.includes('/recall?')) return json({ canRecall: false });
      if (url.endsWith('/approve') || url.endsWith('/reopen')) {
        posts.push({ url, body: JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown> });
        const refusal = refusals.shift();
        if (refusal !== undefined) {
          return json(
            { title: 'refused', status: refusal.status, errorCode: refusal.errorCode, extensions2: { messageKey: refusal.messageKey }, correlationId: 'c' },
            refusal.status,
          );
        }

        return new Response(null, { status: 204 });
      }

      return json({});
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], {
    businessKey: 'DOC-SRC',
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
        <SheetActions documentId={1} sheetDefId={42} sheetName="Composition" periodKey={202601} state={options.state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const ApproveButton = /workflow\.approve\W?$/i;
const ReopenButton = /workflow\.reopen\W?$/i;

async function approveViaDialog(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: ApproveButton }));
  const dialog = await screen.findByRole('dialog');
  // Кнопка підтвердження першого діалогу — остання «workflow.approve» у діалозі.
  const verbs = Array.from(dialog.querySelectorAll('button')).filter((b) => /workflow\.approve\W?$/i.test(b.textContent ?? ''));
  fireEvent.click(verbs[verbs.length - 1] as HTMLButtonElement);
}

describe('SheetActions: вплив Approve на залежні документи (B80 N5)', () => {
  it('a) залежних немає — POST approve одразу, без confirmedImpact, діалогу впливу немає', async () => {
    show({
      state: 'Submitted',
      impacts: [impact({ required: false, policy: 'None', approvedCount: 0, dependentCount: 0, consumers: [] })],
    });

    await approveViaDialog();

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]?.body).not.toHaveProperty('confirmedImpact');
    expect(screen.queryByTestId('source-impact-confirm')).toBeNull();
  });

  it('a2) залежні є, але підтвердження за політикою не потрібне — діалог усе одно показується (рішення 19.11)', async () => {
    show({ state: 'Submitted', impacts: [impact({ required: false, policy: 'None', approvedCount: 0 })] });

    await approveViaDialog();

    const confirm = await screen.findByTestId('source-impact-confirm');
    expect(posts).toHaveLength(0);
    expect(screen.getByTestId('source-impact-policy-info')).toBeTruthy();

    fireEvent.click(confirm);

    await waitFor(() => expect(posts).toHaveLength(1));
  });

  it('b) Warn: діалог впливу; POST лише після підтвердження і з відбитком', async () => {
    show({ state: 'Submitted', impacts: [impact()] });

    await approveViaDialog();

    const confirm = await screen.findByTestId('source-impact-confirm');
    expect(posts).toHaveLength(0);
    expect(screen.getByText(/DOC-LAND/)).toBeTruthy();

    fireEvent.click(confirm);

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]?.body['confirmedImpact']).toEqual({ hash: 'hash-1', reason: null });
    expect(posts[0]?.body['approved']).toBe(true);
  });

  it('c) ConfirmWithReason: причина обов\'язкова і йде в запиті', async () => {
    show({ state: 'Submitted', impacts: [impact({ policy: 'ConfirmWithReason', reasonRequired: true })] });

    await approveViaDialog();

    const confirm = await screen.findByTestId('source-impact-confirm');
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/sourceImpact\.reason\.label/), { target: { value: 'Узгоджено з Land' } });
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]?.body['confirmedImpact']).toEqual({ hash: 'hash-1', reason: 'Узгоджено з Land' });
  });

  it('d) Block: пояснення без кнопки підтвердження, POST не йде', async () => {
    show({ state: 'Submitted', impacts: [impact({ policy: 'Block', blocked: true })] });

    await approveViaDialog();

    expect(await screen.findByTestId('source-impact-blocked')).toBeTruthy();
    expect(screen.queryByTestId('source-impact-confirm')).toBeNull();
    expect(posts).toHaveLength(0);
  });

  it('e) запит впливу впав — дія йде, сервер відмовляє 409, діалог відкривається за відмовою і підтверджується', async () => {
    show({
      state: 'Submitted',
      impacts: ['fail', impact({ impactHash: 'fresh' })],
      refusals: [{ status: 409, errorCode: 'ECR-SRC-0409', messageKey: 'err.ECR-SRC-0409.impactConfirmationRequired' }],
    });

    await approveViaDialog();

    const confirm = await screen.findByTestId('source-impact-confirm');
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).not.toHaveProperty('confirmedImpact');

    fireEvent.click(confirm);

    await waitFor(() => expect(posts).toHaveLength(2));
    expect(posts[1]?.body['confirmedImpact']).toEqual({ hash: 'fresh', reason: null });
  });

  it('f) вплив змінився між оглядом і підтвердженням (409 impactChanged) — діалог оновлюється, а не мовчить', async () => {
    show({
      state: 'Submitted',
      impacts: [impact({ impactHash: 'old' }), impact({ impactHash: 'new', approvedCount: 2, dependentCount: 2 })],
      refusals: [{ status: 409, errorCode: 'ECR-SRC-0409', messageKey: 'err.ECR-SRC-0409.impactChanged' }],
    });

    await approveViaDialog();
    fireEvent.click(await screen.findByTestId('source-impact-confirm'));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]?.body['confirmedImpact']).toEqual({ hash: 'old', reason: null });

    // Діалог лишився відкритим з новим впливом; друге підтвердження несе новий відбиток.
    await waitFor(() => expect(screen.getByTestId('source-impact-confirm')).toBeTruthy());
    await waitFor(() => expect(screen.getByText(/sourceImpact\.intro\.approve/).textContent).toContain('2'));
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    await waitFor(() => expect(posts).toHaveLength(2));
    expect(posts[1]?.body['confirmedImpact']).toEqual({ hash: 'new', reason: null });
  });
});

describe('SheetActions: вплив Reopen із затвердженого на залежні документи (B80 N5)', () => {
  async function reopenViaDialogs(reason: string): Promise<void> {
    fireEvent.click(await screen.findByRole('button', { name: ReopenButton }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(dialog.querySelector('textarea') as HTMLTextAreaElement, { target: { value: reason } });
    const verbs = Array.from(dialog.querySelectorAll('button')).filter((b) => /workflow\.reopen\W?$/i.test(b.textContent ?? ''));
    fireEvent.click(verbs[verbs.length - 1] as HTMLButtonElement);
  }

  it('g) ConfirmWithReason: повернення чекає діалогу впливу; запит несе причину повернення І підтвердження', async () => {
    show({ state: 'Approved', impacts: [impact({ action: 'reopen', policy: 'ConfirmWithReason', reasonRequired: true, impactHash: 'r1' })] });

    await reopenViaDialogs('Виправлення складу');

    await screen.findByTestId('source-impact-confirm');
    expect(posts).toHaveLength(0);
    fireEvent.change(screen.getByLabelText(/sourceImpact\.reason\.label/), { target: { value: 'Акт 12' } });
    fireEvent.click(screen.getByTestId('source-impact-confirm'));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]?.url).toContain('/reopen');
    expect(posts[0]?.body['reason']).toBe('Виправлення складу');
    expect(posts[0]?.body['confirmedImpact']).toEqual({ hash: 'r1', reason: 'Акт 12' });
  });

  it('h) Block: діалог пояснює відмову, POST reopen не йде', async () => {
    show({ state: 'Approved', impacts: [impact({ action: 'reopen', policy: 'Block', blocked: true })] });

    await reopenViaDialogs('Правка');

    expect(await screen.findByTestId('source-impact-blocked')).toBeTruthy();
    expect(posts).toHaveLength(0);
  });
});
