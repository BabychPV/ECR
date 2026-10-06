import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * A2-08 (F-25 / D-285): автор подання бачив «Approve»/«Reject» на власному
 * аркуші, а сервер на погодження відповідає `403 approveOwnSubmission`.
 *
 * ⛔ «Автор» — ознака СЕРВЕРА (`GET …/recall` → `canRecall`), не здогад
 * клієнта. Кожен тест ставить грант `Approve`, за якого кнопки МУСЯТЬ бути;
 * різниться лише відповідь сервера, тож зникнення означає саме авторство.
 */

const DocumentId = 1;
const SheetDefId = 42;
const PeriodKey = 202601;
const ProjectId = 7;

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: ProjectId,
  sheetCount: 1,
  sheetStates: { S1: 'Submitted' },
};

const Me = {
  denies: [],
  grants: { 'Project:7': 'Approve' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

let availabilityAsked = 0;

function show(canRecall: boolean): void {
  availabilityAsked = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) return json(Me);
      if (url.includes('/recall?')) {
        availabilityAsked += 1;
        return json({ canRecall });
      }
      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: автор подання не бачить Approve/Reject (A2-08)', () => {
  it('сервер каже «це твоє подання» (canRecall) — Approve/Reject немає, є Recall', async () => {
    show(true);

    expect(await screen.findByRole('button', { name: RecallButton })).toBeTruthy();
    expect(screen.queryByRole('button', { name: ApproveButton })).toBeNull();
    expect(screen.queryByRole('button', { name: RejectButton })).toBeNull();
  });

  it('не автор (canRecall = false) — погоджувач бачить обидві кнопки', async () => {
    show(false);

    await waitFor(() => {
      expect(availabilityAsked).toBe(1);
    });
    expect(await screen.findByRole('button', { name: ApproveButton })).toBeTruthy();
    expect(screen.getByRole('button', { name: RejectButton })).toBeTruthy();
    expect(screen.queryByRole('button', { name: RecallButton })).toBeNull();
  });
});
