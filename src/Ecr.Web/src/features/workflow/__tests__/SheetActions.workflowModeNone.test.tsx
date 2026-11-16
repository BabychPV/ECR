import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * ✎ B139 (B75): версія шаблону «без подання й затвердження» (`DocumentSummary.workflowMode = 'None'`) — подання,
 * затвердження, повернення й відкликання не показуються; у звичайному режимі — як було.
 *
 * ⛔ Мутація: прибрати `!workflowOff &&` у `useSheetActions` — червоніють обидва тести режиму `None`.
 */
const DocumentId = 1;
const SheetDefId = 42;
const PeriodKey = 202601;

const summary = (state: string, workflowMode: string | null) => ({
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { S1: state },
  workflowMode,
});

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

let meAsked = 0;

function show(state: 'Draft' | 'Submitted', workflowMode: string | null): void {
  meAsked = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/approver-edit')) return json({ canApproverEdit: true });
      if (url.includes('/recall?')) return json({ canRecall: true });
      if (url.includes('/submission-target')) {
        return json({ targetPeriodKey: PeriodKey, nextPeriodKey: null, submittablePeriodKeys: [PeriodKey] });
      }
      if (url.includes('/api/v1/me')) {
        meAsked += 1;
        return json({
          denies: [],
          grants: { 'Project:7': 'Approve' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'author',
        });
      }

      return json(null);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', DocumentId, PeriodKey], summary(state, workflowMode));

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={DocumentId} sheetDefId={SheetDefId} sheetName="Emissions" periodKey={PeriodKey} state={state} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

// ⚠ Каталогу немає: назви кнопок — ключі в маркерах (`⟦workflow.approve⟧`).
const Approve = /workflow\.approve\W?$/i;
const Reject = /workflow\.reject\W?$/i;
const Recall = /workflow\.recall\W?$/i;
const Submit = /workflow\.submit/i;
const Recalculate = /recalculate/i;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: шаблон без подання й затвердження (B139, B75)', () => {
  it('контроль: звичайний режим, поданий аркуш — Approve, Reject і Recall є', async () => {
    show('Submitted', 'SubmitApprove');

    expect(await screen.findByRole('button', { name: Approve })).toBeTruthy();
    expect(screen.getByRole('button', { name: Reject })).toBeTruthy();
    expect(await screen.findByRole('button', { name: Recall })).toBeTruthy();
  });

  it('None, поданий аркуш (перенесений документ) — Approve, Reject і Recall немає', async () => {
    show('Submitted', 'None');

    await waitFor(() => expect(meAsked).toBeGreaterThan(0));
    // Дати запитам і рендеру осісти: у звичайному режимі кнопки вже були б.
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(screen.queryByRole('button', { name: Approve })).toBeNull();
    expect(screen.queryByRole('button', { name: Reject })).toBeNull();
    expect(screen.queryByRole('button', { name: Recall })).toBeNull();
  });

  it('контроль: звичайний режим, чернетка — Submit є', async () => {
    show('Draft', null);

    expect(await screen.findByRole('button', { name: Submit })).toBeTruthy();
  });

  it('None, чернетка — Submit немає, Recalculate лишається', async () => {
    show('Draft', 'None');

    expect(await screen.findByRole('button', { name: Recalculate })).toBeTruthy();
    await waitFor(() => expect(meAsked).toBeGreaterThan(0));
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(screen.queryByRole('button', { name: Submit })).toBeNull();
  });
});
