import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { testTheme } from '@/test/render';
import { SheetCopyBar, type SheetCopyTarget } from '../SheetCopyBar';

/**
 * L9-01 (F13-p144, P3): відмова видалення копії показується ОДИН раз — у `ErrorAlert` панелі.
 * Без `meta.handled` глобальна сітка (`createQueryClient`) кидала ще й тост.
 */

const Copy: SheetCopyTarget = {
  sheetDefId: 10,
  sheetCode: 'GEN',
  label: 'Boiler 2',
  groupNameValues: { en: 'Boiler' },
  sheetCopyId: 77,
  isCopyable: true,
};

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

afterEach(() => {
  cleanup();
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SheetCopyBar: одна відмова видалення — одне сповіщення', () => {
  it('409 DELETE показано в ErrorAlert панелі і не дублюється тостом', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.endsWith('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }], 200);
        if (url.endsWith('/api/v1/documents/42/sheet-copies/77') && init?.method === 'DELETE') {
          return json(
            {
              title: 'Conflict',
              status: 409,
              errorCode: 'ECR-DOC-0409',
              correlationId: 'cid-once',
              detail: 'The copy cannot be deleted: in period 202606 it is Submitted.',
              messageKey: 'err.ECR-DOC-0409.sheetCopyHasWorkflow',
              periodKey: 202606,
              workflowStatus: 'Submitted',
            },
            409,
          );
        }
        return new Response(null, { status: 204 });
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <Notifications />
        <QueryClientProvider client={createQueryClient()}>
          <SheetCopyBar documentId={42} target={Copy} canAdd onSelect={vi.fn()} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(screen.getByTestId('sheet-copy-delete'));
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-DOC-0409');
    // Дати сітці шанс спрацювати: тост з'являється в тому самому циклі, що й помилка.
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(document.querySelectorAll('.mantine-Notification-root')).toHaveLength(0);
  });
});
