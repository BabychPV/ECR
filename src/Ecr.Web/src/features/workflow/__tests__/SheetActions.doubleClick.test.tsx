import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { SheetActions } from '../SheetActions';

/**
 * AN-28 P2-2: поки зберігається набране (кадр + оберт PATCH), Submit не показував
 * зайнятості, і другий клік ставив у чергу другий POST submit
 * (зонд рев'ю: flush, flush, POST submit, POST submit).
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

const log: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/submit')) {
        log.push('POST submit');
        // Сервер відповідає не миттєво: подвійний клік у цей час теж не має пройти.
        await new Promise((resolve) => setTimeout(resolve, 100));
      }

      return json({});
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

/** «Сітка» з набраним, яке зберігається 300 мс. */
function slowDirtyGrid(): void {
  let dirty = true;
  unregister = registerUnsavedSource('an28-p2-2', {
    hasUnsaved: () => dirty,
    flush: async () => {
      log.push('flush');
      await new Promise((resolve) => setTimeout(resolve, 300));
      dirty = false;

      return true;
    },
  });
}

afterEach(() => {
  unregister?.();
  unregister = null;
  log.length = 0;
  vi.unstubAllGlobals();
});

describe('AN-28 P2-2: Submit під час збереження набраного', () => {
  it('кнопка зайнята, повторні кліки не дають другого POST submit', async () => {
    mockFetch();
    slowDirtyGrid();
    show();

    const button = await screen.findByRole('button', { name: /submit/i });
    fireEvent.click(button);
    // Синхронний другий клік - до будь-якого рендеру.
    fireEvent.click(button);

    await waitFor(() => {
      expect((button as HTMLButtonElement).disabled).toBe(true);
    });
    fireEvent.click(button);

    await waitFor(() => {
      expect(log).toEqual(['flush', 'POST submit']);
    });
    // Клік, поки сервер іще відповідає.
    fireEvent.click(button);
    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(log).toEqual(['flush', 'POST submit']);
  });
});
