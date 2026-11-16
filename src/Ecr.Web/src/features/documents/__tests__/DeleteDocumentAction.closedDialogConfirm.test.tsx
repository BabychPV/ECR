import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import { useDeleteDocumentAction } from '@/features/documents/DeleteDocumentAction';
import { testTheme } from '@/test/render';

/**
 * B145: діалог видалення документа, що вже закривається, не шле `DELETE` вдруге.
 *
 * ⚠ Шлях ВІДМОВИ, а не успіху: після успіху сторінка йде на перелік і діалог зникає разом із нею. Після
 * відмови (`409`) діалог закривається, банер лишається — і другий клік подвійного кліку припадав на кнопку
 * діалогу, що ще в DOM на час анімації: друге видалення без підтвердження. Захист — в обгортці
 * `LazyConfirmModal` (`features/workflow/lazyDialogs.tsx`); без нього тут два `DELETE`.
 *
 * ⚠ Без залежності від часу: діалог підмінено кнопкою, яка є в DOM завжди, відповідь сервера віддає тест.
 */
vi.mock('@/shared/ui/ConfirmModal', async () => ({
  ConfirmModal: (await import('@/test/alwaysMountedDialogs')).ConfirmModal,
}));

const DraftDocument: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: 42,
  nameL10n: { values: { en: 'Boiler house' } },
  projectId: 7,
  sheetCount: 1,
  sheetStates: { GEN: 'Draft' },
  hasLateEdits: false,
  stepApproverNamesTruncated: false,
};

const sent: string[] = [];
let respond: (() => void) | null = null;

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = String(init?.method ?? 'GET');
      if (method !== 'DELETE') {
        return new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      sent.push(`DELETE ${String(url)}`);
      await new Promise<void>((resolve) => {
        respond = resolve;
      });

      return new Response(
        JSON.stringify({
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-DOC-0409',
          correlationId: 'cid-409',
          detail: 'Only a draft document can be deleted; sheet 3 for period 202601 is Submitted.',
          messageKey: 'err.ECR-DOC-0409.deleteNotDraft',
          reason: 'Submitted',
        }),
        { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }),
  );
}

function Harness(): JSX.Element {
  const deletion = useDeleteDocumentAction({
    documentId: DraftDocument.id,
    document: DraftDocument,
    sheetCodes: ['GEN'],
    allowed: true,
  });

  return (
    <div>
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

afterEach(() => {
  sent.length = 0;
  respond = null;
  vi.unstubAllGlobals();
});

describe('B145: видалення документа — діалог, що закривається', () => {
  it('клік по «Delete» діалогу після відмови сервера не шле другого DELETE', async () => {
    mockServer();
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/documents/42']}>
            <Harness />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(screen.getByRole('menuitem', { name: '⟦documents.delete⟧' }));
    const confirm = await screen.findByRole('button', { name: 'confirm:⟦documents.delete⟧' });
    expect(confirm.getAttribute('data-opened')).toBe('true');

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(sent).toEqual(['DELETE /api/v1/documents/42']);
    });

    // Сервер відмовив: діалог закрито, банер із причиною на сторінці, запит завершено.
    respond?.();
    await screen.findByRole('alert');
    await waitFor(() => {
      expect(confirm.getAttribute('data-opened')).toBe('false');
      expect(confirm.getAttribute('data-pending')).toBe('false');
    });

    // Клік по кнопці діалогу, що закривається.
    fireEvent.click(confirm);

    // Незахищена мутація стартує одразу: кілька тіків циклу подій — і другий DELETE вже був би в журналі.
    await new Promise<void>((resolve) => setImmediate(resolve));
    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(sent).toEqual(['DELETE /api/v1/documents/42']);
  });
});
