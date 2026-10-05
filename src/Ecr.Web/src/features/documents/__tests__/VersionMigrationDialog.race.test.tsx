import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { VersionMigrationDialog } from '@/features/documents/VersionMigrationDialog';
import { showApiError } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * AN-39 / L8-09 і L8-14.
 *
 * L8-09: поки летить сухий прогін, вибір версії/режиму заблоковано - відповідь для A
 * не може лягти під вибір B (звіт, до того ж, несе ключ вибору).
 * L8-14: відмову показує ErrorAlert у діалозі; глобальна сітка не додає другий тост.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showApiError: vi.fn(),
  showDone: vi.fn(),
}));

let postMode: 'hang' | 'refuse' = 'hang';

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Promise<Response> =>
        Promise.resolve(
          new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': status === 200 ? 'application/json' : 'application/problem+json' },
          }),
        );

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 9, userName: 't' });
      }

      if (url.includes('/migrate-version') && init?.method === 'POST') {
        if (postMode === 'hang') return new Promise<Response>(() => undefined);

        return json({ title: 'x', status: 422, errorCode: 'ECR-REQ-0422', correlationId: 'c' }, 422);
      }

      return json({
        projectId: 7,
        currentVersionId: 1,
        currentVersion: '1.0',
        targets: [
          { id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null },
          { id: 3, version: '3.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null },
        ],
      });
    }),
  );
}

async function openAndPickTarget(): Promise<void> {
  stubServer();
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={createQueryClient()}>
        <VersionMigrationDialog documentId={42} onClose={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const select = await screen.findByRole('textbox', { name: '⟦documents.migrateTarget⟧' });
  await waitFor(() => expect((select as HTMLInputElement).disabled).toBe(false));
  fireEvent.click(select);
  fireEvent.click(await screen.findByRole('option', { name: '2.0' }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showApiError).mockClear();
});

describe('VersionMigrationDialog: гонка сухого прогону і подвійне сповіщення', () => {
  it('L8-09: поки летить сухий прогін, вибір версії й режиму заблоковано', async () => {
    postMode = 'hang';
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await waitFor(() => {
      expect((screen.getByRole('textbox', { name: '⟦documents.migrateTarget⟧' }) as HTMLInputElement).disabled).toBe(true);
    });
    for (const radio of screen.getAllByRole('radio')) {
      expect((radio as HTMLInputElement).disabled).toBe(true);
    }
  });

  it('L8-14: відмова сухого прогону - рядок у діалозі, без другого тосту', async () => {
    postMode = 'refuse';
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await waitFor(() => expect(document.querySelector('[role="alert"]')).not.toBeNull());
    expect(vi.mocked(showApiError)).not.toHaveBeenCalled();
  });
});
