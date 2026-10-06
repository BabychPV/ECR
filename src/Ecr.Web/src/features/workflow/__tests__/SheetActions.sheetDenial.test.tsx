import { describe, it, expect, vi, beforeAll, afterAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { SheetActions } from '../SheetActions';

/**
 * A2-08: відмову погодження `403 ECR-ACCS-0403` панель показує з НАЗВОЮ
 * аркуша, а не тим реченням, яке сервер склав з id («Sheet 42 …»).
 *
 * ⚠ Перевіряє проводку `decide.onError` → `showSheetError`; сам текст —
 * `sheetDenial.test.ts`.
 */

const Strings = {
  'err.ECR-ACCS-0403.approveDenied': 'Sheet {sheetDefId} cannot be approved or rejected: {reason}.',
  'deny.InsufficientGrantLevel': 'Your access level is not sufficient.',
};

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: 1,
  nameL10n: null,
  projectId: 7,
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
  userName: 'approver',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings') || url.includes('/localization')) {
        return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-en-1"' },
        });
      }
      if (url.includes('/api/v1/me')) return json(Me);
      if (url.includes('/recall?')) return json({ canRecall: false });
      if (url.includes('/approve') && init?.method === 'POST') {
        return json(
          {
            title: 'Forbidden',
            status: 403,
            detail: 'Sheet 42 cannot be approved or rejected: Your access level is not sufficient.',
            errorCode: 'ECR-ACCS-0403',
            correlationId: 'c-1',
            messageKey: 'err.ECR-ACCS-0403.approveDenied',
            sheetDefId: '42',
            reason: 'InsufficientGrantLevel',
            reasonKey: 'deny.InsufficientGrantLevel',
          },
          403,
        );
      }
      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
  await loadCatalog('en', 'public');

  // ⚠ Прогрів lazy-діалогу (як `SourceEventsTab.lazy.test.tsx`): перший імпорт
  // чанка під навантаженням повного прогону інакше з'їдав таймаут findBy.
  await import('@/shared/ui/ConfirmModal');
  await import('../sheetDenial');
});

afterAll(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('SheetActions: відмова погодження з назвою аркуша (A2-08)', () => {
  it('403 approveDenied — тост «Sheet «Emissions» …», а не «Sheet 42 …»', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['document', 1, 202601], Summary);

    render(
      <MantineProvider>
        <Notifications />
        <QueryClientProvider client={client}>
          <SheetActions documentId={1} sheetDefId={42} sheetName="Emissions" periodKey={202601} state="Submitted" />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.approve\W?$/i }));
    const dialog = await screen.findByRole('dialog', {}, { timeout: 10_000 });
    const confirm = Array.from(dialog.querySelectorAll('button')).find((b) => /workflow\.approve/.test(b.textContent ?? ''));
    fireEvent.click(confirm as HTMLElement);

    expect(
      await screen.findByText(
        'Sheet «Emissions» cannot be approved or rejected: Your access level is not sufficient.',
        {},
        { timeout: 10_000 },
      ),
    ).toBeTruthy();
    expect(screen.queryByText(/Sheet 42/)).toBeNull();
  });
});
