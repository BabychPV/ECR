import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MeQueryKey } from '@/shared/session/useSession';
import { SheetActions } from '../SheetActions';

/**
 * PB2-P2-2 (D-444 пп. 2–3): «Approve»/«Reject» на ПОДАНОМУ аркуші показує відповідь сервера
 * (`GET …/approver-edit` → `canApproverEdit`, те саме правило, що й `POST …/approve`), а не грант із `/me`.
 *
 * Дефект: учасник документа БЕЗ маршруту з правом Approve (гранта Approve у `/me` немає) не мав кнопок, хоча
 * сервер його пропускає (approve → 204); погоджувач ІНШОГО кроку маршруту (грант Approve є) бачив кнопки й
 * отримував 403. Поки відповіді немає — кнопок немає (відмова закрита); у симуляції — теж немає.
 *
 * ⛔ Мутація (прогнано локально): у `SheetActions.tsx` замінити `approverCheck.data?.canApproverEdit === true` у
 * `mayDecide` на старе `mayWorkflow('approve')` — червоніють a) (кнопок немає) і b) (кнопки є).
 */

const DocumentId = 1;
const SheetDefId = 42;
const PeriodKey = 202601;
const ProjectId = 7;

const ApproverEditKey = ['document', DocumentId, PeriodKey, 'approver-edit', SheetDefId];

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: ProjectId,
  sheetCount: 1,
  sheetStates: { S1: 'Submitted' },
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

let approverEditAsked = 0;
let client: QueryClient;

/**
 * @param server `true`/`false` — відповідь `canApproverEdit`; `'pending'` — відповідь не приходить.
 */
function show(options: { grants: Record<string, string>; server: boolean | 'pending'; isSimulation?: boolean }): void {
  approverEditAsked = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      // ⚠ Раніше за ширші збіги: `/approver-edit` іде під шляхом документа.
      if (url.includes('/approver-edit')) {
        approverEditAsked += 1;
        if (options.server === 'pending') return new Promise<Response>(() => undefined);
        return json({ canApproverEdit: options.server });
      }
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: options.grants,
          isSimulation: options.isSimulation ?? false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'participant',
        });
      }
      // Якір: «Recall» не залежить ні від гранта, ні від симуляції — компонент відрендерився.
      if (url.includes('/recall?')) return json({ canRecall: true });
      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', DocumentId, PeriodKey], Summary);

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions
          documentId={DocumentId}
          sheetDefId={SheetDefId}
          sheetName="Emissions"
          periodKey={PeriodKey}
          state="Submitted"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

// ⚠ Каталогу немає: назви кнопок — ключі в маркерах (`⟦workflow.approve⟧`).
const ApproveButton = /workflow\.approve\W?$/i;
const RejectButton = /workflow\.reject\W?$/i;
const RecallButton = /workflow\.recall\W?$/i;

/** Профіль доїхав, сервер спитано (і, якщо не `pending`, відповів), панель перемальована. */
async function settled(expectAnswer: boolean): Promise<void> {
  expect(await screen.findByRole('button', { name: RecallButton })).toBeTruthy();
  await waitFor(() => {
    expect(client.getQueryState(MeQueryKey)?.status).toBe('success');
    expect(approverEditAsked).toBeGreaterThan(0);
    if (expectAnswer) expect(client.getQueryState(ApproverEditKey)?.status).toBe('success');
  });
  // Один цикл подій — щоб рендер після відповіді встиг відбутися.
  await new Promise((resolve) => setTimeout(resolve, 50));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: Approve/Reject на поданому аркуші — за відповіддю сервера (PB2-P2-2)', () => {
  it('a) учасник документа без маршруту, гранта Approve у /me немає, сервер: так — Approve і Reject є', async () => {
    show({ grants: { 'Project:7': 'Read' }, server: true });

    expect(await screen.findByRole('button', { name: ApproveButton })).toBeTruthy();
    expect(screen.getByRole('button', { name: RejectButton })).toBeTruthy();
  });

  it('b) грант Approve є, але сервер: ні (погоджувач іншого кроку) — ні Approve, ні Reject', async () => {
    show({ grants: { 'Project:7': 'Approve' }, server: false });
    await settled(true);

    expect(screen.queryByRole('button', { name: ApproveButton })).toBeNull();
    expect(screen.queryByRole('button', { name: RejectButton })).toBeNull();
  });

  it('c) відповідь сервера ще в дорозі — кнопок немає (відмова закрита)', async () => {
    show({ grants: { 'Project:7': 'Approve' }, server: 'pending' });
    await settled(false);

    expect(client.getQueryState(ApproverEditKey)?.status).toBe('pending');
    expect(screen.queryByRole('button', { name: ApproveButton })).toBeNull();
    expect(screen.queryByRole('button', { name: RejectButton })).toBeNull();
  });

  it('d) симуляція «очима користувача», сервер: так — кнопок немає', async () => {
    show({ grants: { 'Project:7': 'Approve' }, server: true, isSimulation: true });
    await settled(true);

    expect(screen.queryByRole('button', { name: ApproveButton })).toBeNull();
    expect(screen.queryByRole('button', { name: RejectButton })).toBeNull();
  });
});
