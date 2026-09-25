import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import {
  isKnownDraft,
  useDeleteDocumentAction,
} from '@/features/documents/DeleteDocumentAction';
import { showDone } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * «Видалити документ-чернетку» — показ кнопки, підтвердження, успіх і відмова.
 *
 * ⛔ Сервер — справжній `fetch`-стаб із тілом `application/problem+json`, а не
 * підмінений `deleteDocument`: так через тест іде той самий розбір відмови
 * (`apiFetch` → `EcrApiError` → `problemText`), що й у продукті, і «причина
 * з `messageKey`» перевіряється, а не підставляється.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DraftDocument: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: 42,
  nameL10n: { values: { en: 'Boiler house' } },
  projectId: 7,
  sheetCount: 2,
  sheetStates: { GEN: 'Draft', AIR: 'Draft' },
  hasLateEdits: false,
};

const sent: { url: string; method: string }[] = [];

function mockServer(status: number, body?: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });

      return body === undefined
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': 'application/problem+json' },
          });
    }),
  );
}

function Harness(props: {
  document: DocumentSummary;
  allowed: boolean;
  sheetCodes: readonly string[];
}): JSX.Element {
  const deletion = useDeleteDocumentAction({ documentId: props.document.id, ...props });

  return (
    <div>
      <h1>{props.document.businessKey}</h1>
      {/* ⚠ Пункт живе в меню «More» сторінки (`DocumentToolbar`); тут меню
          відкрите завжди, щоб перевіряти сам пункт, а не механіку меню. */}
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <span>menu</span>
        </Menu.Target>
        <Menu.Dropdown>{deletion.menuItem}</Menu.Dropdown>
      </Menu>
      {deletion.dialog}
      {deletion.refusal}
    </div>
  );
}

function show(
  options: {
    document?: DocumentSummary;
    allowed?: boolean;
    sheetCodes?: readonly string[];
  } = {},
): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const document = options.document ?? DraftDocument;

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/documents/${String(document.id)}`]}>
          <Routes>
            <Route path="/" element={<div data-testid="documents-list" />} />
            <Route
              path="/documents/:id"
              element={
                <Harness
                  document={document}
                  allowed={options.allowed ?? true}
                  sheetCodes={options.sheetCodes ?? ['GEN', 'AIR']}
                />
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const DeleteButton = { name: '⟦documents.delete⟧' };

async function confirmDeletion(): Promise<void> {
  fireEvent.click(screen.getByRole('menuitem', DeleteButton));
  fireEvent.click(await screen.findByTestId('confirm-verb'));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
});

describe('isKnownDraft', () => {
  it('усі аркуші Draft — чернетка; аркуш без запису стану теж Draft', () => {
    expect(isKnownDraft({ GEN: 'Draft' }, ['GEN', 'AIR'])).toBe(true);
  });

  it.each(['Submitted', 'Approved', 'Rejected'])('хоч один аркуш %s — не чернетка', (state) => {
    expect(isKnownDraft({ GEN: 'Draft', AIR: state }, ['GEN', 'AIR'])).toBe(false);
  });

  it('стан аркуша поза переліком таблиць теж рахується', () => {
    expect(isKnownDraft({ GEN: 'Draft', OLD: 'Submitted' }, ['GEN'])).toBe(false);
  });
});

describe('useDeleteDocumentAction: показ кнопки', () => {
  it('є право і чернетка — кнопка є', () => {
    show();

    expect(screen.getByRole('menuitem', DeleteButton)).toBeDefined();
  });

  it('без права Document.Delete — кнопки НЕМАЄ', () => {
    show({ allowed: false });

    // Спершу — що харнес узагалі намальований, інакше «кнопки немає» було б
    // правдою з іншої причини.
    expect(screen.getByRole('heading', { name: 'DOC-0042' })).toBeDefined();
    expect(screen.queryByRole('menuitem', DeleteButton)).toBeNull();
  });

  it('не чернетка (аркуш поданий) — кнопки НЕМАЄ', () => {
    show({
      document: { ...DraftDocument, sheetStates: { GEN: 'Draft', AIR: 'Submitted' } },
    });

    expect(screen.getByRole('heading', { name: 'DOC-0042' })).toBeDefined();
    expect(screen.queryByRole('menuitem', DeleteButton)).toBeNull();
  });
});

describe('useDeleteDocumentAction: підтвердження', () => {
  it('діалог названий документом, фокус на Cancel, дієслово на кнопці, кнопка небезпечна', async () => {
    mockServer(204);
    show();

    fireEvent.click(screen.getByRole('menuitem', DeleteButton));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Boiler house · DOC-0042');

    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel')));

    const verb = screen.getByTestId('confirm-verb');
    expect(verb.textContent).toBe('⟦documents.delete⟧');
    expect(verb.getAttribute('style') ?? '').toContain('statusError');

    // ⚠ Відкриття діалогу нічого не надсилає: видаляє лише підтвердження.
    expect(sent).toEqual([]);
  });
});

describe('useDeleteDocumentAction: успіх', () => {
  it('DELETE, інвалідизація переліку, сповіщення «видалено», перехід на перелік', async () => {
    mockServer(204);
    const client = show();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await confirmDeletion();

    await screen.findByTestId('documents-list');

    expect(sent).toEqual([{ url: '/api/v1/documents/42', method: 'DELETE' }]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['documents'] });
    expect(showDone).toHaveBeenCalledWith(
      '⟦documents.deleted (name=Boiler house · DOC-0042)⟧',
    );
  });
});

describe('useDeleteDocumentAction: відмова сервера', () => {
  it('409 deleteNotDraft — банер із ПРИЧИНОЮ сервера й кодом, документ лишається', async () => {
    mockServer(409, {
      title: 'Conflict',
      status: 409,
      errorCode: 'ECR-DOC-0409',
      correlationId: 'cid-409',
      detail: 'Only a draft document can be deleted; sheet 3 for period 202601 is Submitted.',
      messageKey: 'err.ECR-DOC-0409.deleteNotDraft',
      reason: 'Submitted',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain(
      'Only a draft document can be deleted; sheet 3 for period 202601 is Submitted.',
    );
    expect(alert.textContent).toContain('ECR-DOC-0409');

    expect(screen.queryByTestId('documents-list')).toBeNull();
    expect(screen.getByRole('heading', { name: 'DOC-0042' })).toBeDefined();
    expect(showDone).not.toHaveBeenCalled();
  });

  it('409 deleteHasHistory — причина «вже проходив погодження», а не загальна відмова', async () => {
    mockServer(409, {
      title: 'Conflict',
      status: 409,
      errorCode: 'ECR-DOC-0409',
      correlationId: 'cid-hist',
      detail: 'Only a draft document can be deleted; this document has already been through approval.',
      messageKey: 'err.ECR-DOC-0409.deleteHasHistory',
      reason: 'WorkflowHistory',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('has already been through approval');
    expect(screen.queryByTestId('documents-list')).toBeNull();
  });

  it('403 noProjectWriteGrant — причина з messageKey, документ лишається', async () => {
    mockServer(403, {
      title: 'Forbidden',
      status: 403,
      errorCode: 'ECR-AUTH-0403',
      correlationId: 'cid-403',
      detail: 'You have no write grant on project 7.',
      messageKey: 'err.ECR-AUTH-0403.noProjectWriteGrant',
      projectId: '7',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('You have no write grant on project 7.');
    expect(alert.textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByTestId('documents-list')).toBeNull();
  });
});
