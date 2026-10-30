import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { VersionMigrationDialog, itemKindText } from '@/features/documents/VersionMigrationDialog';
import { testTheme } from '@/test/render';

/**
 * ✎ D-461: звіт сухого прогону переносу версії каже, скільки ручних значень замість формул НЕ переїде ручними
 * (`lostFormulaOverrides`; формула цільової версії ручного значення не дозволяє або колонки немає), і перелічує
 * їх пунктами `kind = 'OverrideLost'` із власним підписом — як решту видів. Перенос це не блокує.
 *
 * ⚠ Сервер підмінено на рівні `fetch` (як `VersionMigrationDialog.backup.test.tsx`).
 */
let lostFormulaOverrides: number | null = null;

function report(): Record<string, unknown> {
  return {
    documentId: 42,
    projectId: 7,
    documentCount: 1,
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
    lockedSheetItemsTruncated: false,
    lostFormulaOverrides,
    items:
      lostFormulaOverrides === null
        ? []
        : [{ path: 'S1.T1.C7', kind: 'OverrideLost', changeClass: 'Presentation', values: lostFormulaOverrides }],
    itemsTruncated: false,
    otherProjectsOnSourceVersion: 0,
    otherDocumentsOnSourceVersion: 0,
  };
}

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Promise<Response> =>
        Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 9, userName: 't' });
      }

      if (url.includes('/migrate-version') && init?.method === 'POST') return json(report());

      return json({
        projectId: 7,
        currentVersionId: 1,
        currentVersion: '1.0',
        targets: [{ id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null }],
      });
    }),
  );
}

async function openAndDryRun(): Promise<void> {
  stubServer();
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={createQueryClient()}>
        <VersionMigrationDialog documentId={42} onClose={() => undefined} jobId={null} onJobChange={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const select = await screen.findByRole('textbox', { name: '⟦documents.migrateTarget⟧' });
  await waitFor(() => expect((select as HTMLInputElement).disabled).toBe(false));
  fireEvent.click(select);
  fireEvent.click(await screen.findByRole('option', { name: '2.0' }));
  fireEvent.click(screen.getByTestId('migrate-dry-run'));
  await screen.findByText('⟦documents.migrateCanApply⟧');
}

afterEach(() => {
  vi.unstubAllGlobals();
  lostFormulaOverrides = null;
});

describe('VersionMigrationDialog: ручні значення замість формул, що не переїдуть (D-461)', () => {
  it('itemKindText: OverrideLost — підписом каталогу', () => {
    expect(itemKindText('OverrideLost')).toBe('⟦documents.migrateKindOverrideLost⟧');
  });

  it('lostFormulaOverrides > 0 — рядок-попередження з числом і пункт OverrideLost у переліку; застосувати можна', async () => {
    lostFormulaOverrides = 3;
    await openAndDryRun();

    const warning = document.querySelector('[data-migrate-lost-formula-overrides]');
    expect(warning?.textContent).toBe('⟦documents.migrateLostFormulaOverrides (count=3)⟧');

    const table = screen.getByRole('table', { name: '⟦documents.migrateVersionTitle⟧' });
    const row = within(table).getByRole('row', { name: /S1\.T1\.C7/ });
    expect(within(row).getByText('⟦documents.migrateKindOverrideLost⟧')).toBeTruthy();
    expect(within(row).getByText('3')).toBeTruthy();

    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(false);
  });

  it('null (таких немає) — рядка-попередження немає', async () => {
    lostFormulaOverrides = null;
    await openAndDryRun();

    expect(document.querySelector('[data-migrate-lost-formula-overrides]')).toBeNull();
  });
});
