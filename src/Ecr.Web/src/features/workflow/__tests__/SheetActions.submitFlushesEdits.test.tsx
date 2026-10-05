import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { SheetActions } from '../SheetActions';

/**
 * AN-28 / L8-01: Submit (і решта дій над документом) спершу зберігають
 * набране в сітці. Дефект: POST /submit ішов раніше за PATCH, сервер подавав
 * без значення, а пізніший PATCH отримував 403 `DocumentSubmitted`.
 */

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Manage' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

/** Хронологія подій: збереження незбереженого і мережеві дії. */
const log: string[] = [];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(CurrentUser), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/submit')) {
        log.push('POST submit');

        return new Response(JSON.stringify({}), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify({}), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
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
        <SheetActions documentId={1} sheetDefId={2} periodKey={202601} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

let unregister: (() => void) | null = null;

/** Реєструє «сітку» з незбереженою правкою; `ok` - чи вдасться її зберегти. */
function dirtyGrid(ok: boolean): void {
  unregister = registerUnsavedSource('an28-test', {
    hasUnsaved: () => true,
    flush: async () => {
      log.push('flush');
      await new Promise((resolve) => setTimeout(resolve, 20));

      return ok;
    },
  });
}

afterEach(() => {
  unregister?.();
  unregister = null;
  log.length = 0;
  vi.unstubAllGlobals();
});

describe('AN-28 L8-01: Submit зберігає незбережене', () => {
  it('зберігач викликано ДО POST submit', async () => {
    mockFetch();
    dirtyGrid(true);
    show();

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));

    await waitFor(() => {
      expect(log).toEqual(['flush', 'POST submit']);
    });
  });

  it('збереження відмовило - POST submit не відправлено', async () => {
    mockFetch();
    dirtyGrid(false);
    show();

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));

    await waitFor(() => {
      expect(log).toEqual(['flush']);
    });
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(log).toEqual(['flush']);
  });
});
