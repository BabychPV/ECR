import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { useVersionMigrationAction } from '@/features/documents/VersionMigrationAction';
import { settle } from '@/test/closedDialogProbe';
import { testTheme } from '@/test/render';

/**
 * B149: «Migrate to a new template version» — один перенос на відкриття, навіть поки сторінка перечитує все після успіху.
 *
 * ⛔ Зонд на рівні власника (`useVersionMigrationAction`, база 826f654e4): задача переносу `Succeeded` → власник
 * (`useMigrationJobCompletion`) чекає `invalidateQueries()` УСІХ запитів і лише потім закриває діалог. Увесь цей час
 * діалог ВІДКРИТИЙ: задача вже не «йде» (`isActiveJob` хибний), звіт сухого прогону «можна» на місці, «Migrate»
 * активна — клік ставив другий `POST /api/v1/documents/42/migrate-version` (`dryRun: false`) на ту саму версію.
 *
 * Захист — у діалозі: після успішної задачі цього відкриття «Migrate» нічого не шле (звільняється лише відмовою задачі
 * чи зміною вибору — тоді `jobId` скидається).
 *
 * Мутаційний доказ: прибрати `jobState === 'Succeeded' ||` у `VersionMigrationDialog.tsx` → червоний
 * (`expected [ { targetVersionId: 2, …(3) }, …(1) ] to have a length of 1 but got 2`).
 */

const DocumentId = 42;

const Doc: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { GEN: 'Draft' },
  hasLateEdits: false,
  stepApproverNamesTruncated: false,
};

const Report = {
  documentId: DocumentId, projectId: 7, documentCount: 1, fromVersionId: 1, fromVersion: '1.0', toVersionId: 2, toVersion: '2.0',
  mode: 'Safe', dryRun: true, applied: false, canApply: true, refusals: [], transferredValues: 1, lostValues: 0, guardedValues: 0,
  lockedSheets: 0, lockedSheetsCarried: 0, lockedSheetsAffected: 0, lockedSheetItemsTruncated: false, items: [], itemsTruncated: false,
  mappingsRemapped: 0, mappingsUnmapped: 0, otherProjectsOnSourceVersion: 0, otherDocumentsOnSourceVersion: 0,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function Harness(): JSX.Element {
  const action = useVersionMigrationAction({ documentId: DocumentId, document: Doc });

  return (
    <div>
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <button type="button">menu</button>
        </Menu.Target>
        <Menu.Dropdown>{action.menuItem}</Menu.Dropdown>
      </Menu>
      {action.dialog}
    </div>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: VersionMigrationDialog — один перенос, поки сторінка перечитує все після успіху', () => {
  it('задача Succeeded, діалог ще відкритий (перечитування) — «Migrate» не ставить другий перенос', async () => {
    const applies: unknown[] = [];
    let jobDone = false;
    // Перечитування після успіху тримає тест: діалог лишається відкритим, доки його не відпустять.
    let releaseRefetch: () => void = () => undefined;
    const refetchHeld = new Promise<void>((resolve) => {
      releaseRefetch = resolve;
    });

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = String(init?.method ?? 'GET').toUpperCase();

        if (url.includes('/api/v1/me')) {
          return json({
            denies: [], grants: { 'Project:7': 'Manage' }, isSimulation: false, language: 'en', mustChangePassword: false,
            permissions: ['Template.Edit'], simulatedForUserId: null, userId: 9, userName: 'tester',
          });
        }
        if (url.includes('/migrate-version') && method === 'GET') {
          if (jobDone) await refetchHeld;
          return json({
            projectId: 7, currentVersionId: 1, currentVersion: '1.0',
            targets: [{ id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null }],
          });
        }
        if (url.includes('/migrate-version') && method === 'POST') {
          const body = JSON.parse(String(init?.body)) as { dryRun: boolean };
          if (body.dryRun) return json(Report);
          applies.push(body);
          return json({ jobId: 'IMigrateDocumentVersionJob-abc', documentId: DocumentId }, 202);
        }
        if (url.includes('/api/v1/jobs/')) {
          jobDone = true;
          return json({ jobId: 'IMigrateDocumentVersionJob-abc', state: 'Succeeded', percent: 100, message: 'Migrated', error: null });
        }

        throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Harness />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦documents.migrateVersion⟧' }));
    const select = await screen.findByRole('textbox', { name: '⟦documents.migrateTarget⟧' });
    await waitFor(() => {
      expect((select as HTMLInputElement).disabled).toBe(false);
    });
    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: '2.0' }));
    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    await screen.findByText('⟦documents.migrateCanApply⟧');

    const apply = screen.getByTestId('migrate-apply') as HTMLButtonElement;
    await waitFor(() => {
      expect(apply.disabled).toBe(false);
    });
    fireEvent.click(apply);
    await waitFor(() => {
      expect(applies).toHaveLength(1);
    });

    // Задача завершилась, сторінка перечитує все — діалог ще відкритий, кнопка знову активна.
    await waitFor(() => {
      expect(jobDone).toBe(true);
      expect(apply.disabled).toBe(false);
    });
    expect(screen.getByRole('dialog')).toBeDefined();
    fireEvent.click(apply);
    // ⚠ «Migrate» спершу зберігає набране (`useSettledAction`: кадр + flush) і лише потім шле — кнопка «зайнята»
    // (`data-loading`) увесь цей шлях. Кінець зайнятості — момент, коли незахищений клік уже дійшов би до сервера.
    await waitFor(() => {
      expect(apply.getAttribute('data-loading')).toBeNull();
    });
    await settle();
    expect(applies).toHaveLength(1);

    releaseRefetch();
    await waitFor(() => {
      expect(apply.isConnected).toBe(false);
    });
    expect(applies).toHaveLength(1);
  });
});
