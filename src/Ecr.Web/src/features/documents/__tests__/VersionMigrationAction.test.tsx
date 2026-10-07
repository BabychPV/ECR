import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { useVersionMigrationAction } from '@/features/documents/VersionMigrationAction';
import type { VersionMigrationReport } from '@/features/documents/versionMigrationApi';
import { showDone } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * «Перенести на нову версію шаблону» (ФВ-7.5): пункт за правом, сухий прогін
 * перед переносом і кнопка переносу, що вмикається лише після звіту «можна».
 *
 * ⛔ Сервер — `fetch`-стаб, як у `BusinessKeyChangeAction.test.tsx`: через тест
 * іде той самий шлях `apiFetch`, що й у продукті.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DocumentId = 42;

const Document: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { GEN: 'Draft' },
  hasLateEdits: false,
};

function me(permissions: string[], grant = 'Manage'): unknown {
  return {
    denies: [],
    grants: { 'Project:7': grant },
    isSimulation: false,
    language: 'en',
    mustChangePassword: false,
    permissions,
    simulatedForUserId: null,
    userId: 9,
    userName: 'tester',
  };
}

function report(overrides: Partial<VersionMigrationReport>): VersionMigrationReport {
  return {
    documentId: DocumentId,
    projectId: 7,
    documentCount: 3,
    fromVersionId: 1,
    fromVersion: '1.0',
    toVersionId: 2,
    toVersion: '2.0',
    mode: 'Safe',
    dryRun: true,
    applied: false,
    canApply: true,
    refusals: [],
    transferredValues: 10,
    lostValues: 0,
    guardedValues: 0,
    lockedSheets: 0,
    items: [{ path: 'GEN.T1.C4', kind: 'Added', changeClass: 'Safe', values: 0 }],
    itemsTruncated: false,
    ...overrides,
  };
}

interface Sent {
  readonly method: string;
  readonly body: { targetVersionId: number; mode: string; dryRun: boolean } | undefined;
}

const sent: Sent[] = [];

function mockServer(permissions: string[], dryRunReport: VersionMigrationReport, grant = 'Manage'): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) return json(me(permissions, grant));

      if (url.includes('/migrate-version') && method === 'GET') {
        return json({
          projectId: 7,
          currentVersionId: 1,
          currentVersion: '1.0',
          targets: [
            { id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null },
          ],
        });
      }

      if (url.includes('/migrate-version') && method === 'POST') {
        const body = JSON.parse(String(init?.body)) as Sent['body'];
        sent.push({ method, body });
        return json(body?.dryRun === true ? dryRunReport : { ...dryRunReport, dryRun: false, applied: true });
      }

      throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
    }),
  );
}

function Harness(): JSX.Element {
  const action = useVersionMigrationAction({ documentId: DocumentId, document: Document });

  return (
    <div>
      <h1>doc</h1>
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <span>menu</span>
        </Menu.Target>
        <Menu.Dropdown>{action.menuItem}</Menu.Dropdown>
      </Menu>
      {action.dialog}
    </div>
  );
}

function show(permissions: string[], dryRunReport: VersionMigrationReport, grant = 'Manage'): void {
  mockServer(permissions, dryRunReport, grant);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openAndPickTarget(): Promise<void> {
  fireEvent.click(await screen.findByRole('menuitem', { name: '⟦documents.migrateVersion⟧' }));

  const select = await screen.findByRole('textbox', { name: '⟦documents.migrateTarget⟧' });
  await waitFor(() => expect((select as HTMLInputElement).disabled).toBe(false));
  fireEvent.click(select);
  fireEvent.click(await screen.findByRole('option', { name: '2.0' }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
});

describe('useVersionMigrationAction', () => {
  it('без права Template.Edit пункту немає', async () => {
    show(['Document.View'], report({}));

    await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(screen.queryByRole('menuitem', { name: '⟦documents.migrateVersion⟧' })).toBeNull();
  });

  it('Template.Edit без гранта Manage на проєкт (Write, Approve) — пункту немає; з Manage — є', async () => {
    for (const grant of ['Write', 'Approve']) {
      show(['Template.Edit'], report({}), grant);
      await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(screen.queryByRole('menuitem', { name: '⟦documents.migrateVersion⟧' })).toBeNull();
      cleanup();
    }

    show(['Template.Edit'], report({}), 'Manage');
    expect(await screen.findByRole('menuitem', { name: '⟦documents.migrateVersion⟧' })).toBeDefined();
  });

  it('перенос недоступний до сухого прогону; після звіту «можна» — переносить тим самим вибором', async () => {
    show(['Template.Edit'], report({}));
    await openAndPickTarget();

    const apply = screen.getByTestId('migrate-apply');
    expect((apply as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    await screen.findByText('⟦documents.migrateCanApply⟧');

    // Перемикач режиму і таблиця змін мають доступні назви (WCAG 1.3.1 / 4.1.2).
    expect(screen.getByRole('radiogroup', { name: '⟦documents.migrateVersionTitle⟧' })).toBeDefined();
    expect(screen.getByRole('table', { name: '⟦documents.migrateVersionTitle⟧' })).toBeDefined();
    expect(sent).toEqual([{ method: 'POST', body: { targetVersionId: 2, mode: 'Safe', dryRun: true } }]);

    await waitFor(() => expect((apply as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(apply);

    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual({ method: 'POST', body: { targetVersionId: 2, mode: 'Safe', dryRun: false } });
    await waitFor(() => expect(vi.mocked(showDone)).toHaveBeenCalledTimes(1));
  });

  it('звіт «не можна» показує причину, і перенос лишається вимкненим', async () => {
    show(['Template.Edit'], report({ canApply: false, refusals: ['dataLoss'], lostValues: 5 }));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await screen.findByText('⟦documents.migrateRefusalDataLoss⟧');
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(true);
  });

  it('причина grantsNotMapped показується підписом каталогу, а не сирим кодом (ent7 P3-4)', async () => {
    show(['Template.Edit'], report({ canApply: false, refusals: ['grantsNotMapped'] }));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await screen.findByText('⟦documents.migrateRefusalGrantsNotMapped⟧');
    expect(screen.queryByText('grantsNotMapped')).toBeNull();
  });

  it('причина bindingsNotMapped показується підписом каталогу, а не сирим кодом (D-13)', async () => {
    show(['Template.Edit'], report({ canApply: false, refusals: ['bindingsNotMapped'] }));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await screen.findByText('⟦documents.migrateRefusalBindingsNotMapped⟧');
    expect(screen.queryByText('bindingsNotMapped')).toBeNull();
  });

  it('blockedGrantCount показується рядком із кількістю; без нього рядка немає (ent7 P3-4)', async () => {
    show(['Template.Edit'], report({ canApply: false, refusals: ['grantsNotMapped'], blockedGrantCount: 3 }));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    const line = await waitFor(() => {
      const el = document.querySelector('[data-migrate-blocked-grants]');
      expect(el).not.toBeNull();
      return el as HTMLElement;
    });
    expect(line.textContent).toContain('documents.migrateGrantsNotMappedCount');
  });

  it('без blockedGrantCount рядка про заблоковані гранти немає', async () => {
    show(['Template.Edit'], report({ canApply: false, refusals: ['grantsNotMapped'] }));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await screen.findByText('⟦documents.migrateRefusalGrantsNotMapped⟧');
    expect(document.querySelector('[data-migrate-blocked-grants]')).toBeNull();
  });

  it('зміна режиму після звіту знецінює звіт — перенос знову вимкнено', async () => {
    show(['Template.Edit'], report({}));
    await openAndPickTarget();

    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    await screen.findByText('⟦documents.migrateCanApply⟧');
    await waitFor(() => expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(false));

    fireEvent.click(screen.getByText('⟦documents.migrateModePresentation⟧'));

    await waitFor(() => expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(true));
    expect(screen.queryByText('⟦documents.migrateCanApply⟧')).toBeNull();
  });
});
