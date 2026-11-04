import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { VersionMigrationDialog } from '@/features/documents/VersionMigrationDialog';
import { testTheme } from '@/test/render';

/**
 * RPT-STABLE Part B: обсяг переносу «увесь проєкт» / «з місяця X» у діалозі переносу версії.
 * Вибір показується лише за `fromMonthEnabled`; «з місяця» без X не дає запустити сухий прогін; X і обсяг
 * ідуть у запит; ефективний період і попередження про січень (Q-17) показує звіт сухого прогону.
 */
let flag = true;
let shifted = false;
const posts: Record<string, unknown>[] = [];

function report(): Record<string, unknown> {
  return {
    documentId: 42, projectId: 7, documentCount: 1, fromVersionId: 1, fromVersion: '1.0', toVersionId: 2, toVersion: '2.0',
    mode: 'Safe', dryRun: true, applied: false, canApply: true, refusals: [], transferredValues: 5, lostValues: 0, guardedValues: 0,
    lockedSheets: 0, lockedSheetItemsTruncated: false, items: [], itemsTruncated: false,
    otherProjectsOnSourceVersion: 0, otherDocumentsOnSourceVersion: 0,
    range: 'FromMonth', fromPeriodKey: 202607, effectiveFromPeriodKey: shifted ? 202601 : 202607,
    warnings: shifted ? [{ code: 'effectiveFromShiftedToJanuary', messageKey: 'documents.migrateWarnEffectiveFromJanuary', periodKey: 202601 }] : null,
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

      if (url.includes('/migrate-version') && init?.method === 'POST') {
        posts.push(JSON.parse(String(init.body)) as Record<string, unknown>);

        return json(report());
      }

      return json({
        projectId: 7, currentVersionId: 1, currentVersion: '1.0', fromMonthEnabled: flag,
        targets: [{ id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null }],
      });
    }),
  );
}

async function open(): Promise<void> {
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
}

afterEach(() => {
  vi.unstubAllGlobals();
  posts.length = 0;
  flag = true;
  shifted = false;
});

describe('VersionMigrationDialog: обсяг переносу (RPT-STABLE Part B)', () => {
  it('прапор вимкнено - вибору обсягу немає, запит без range', async () => {
    flag = false;
    await open();

    expect(screen.queryByTestId('migrate-range')).toBeNull();
    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    await screen.findByText('⟦documents.migrateCanApply⟧');
    expect(posts[0]).not.toHaveProperty('range');
  });

  it('«З місяця» без X не дає запустити сухий прогін; з X - range і fromPeriodKey у запиті, ефективний період у звіті', async () => {
    await open();

    const range = await screen.findByTestId('migrate-range');
    expect(range).toBeTruthy();
    fireEvent.click(screen.getByRole('radio', { name: '⟦documents.migrateRangeFromMonth⟧' }));
    expect((screen.getByTestId('migrate-dry-run') as HTMLButtonElement).disabled).toBe(true);

    const field = document.querySelector('[data-migrate-range-block] .ecr-period-field input') as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '202607' } });
    await waitFor(() => expect((screen.getByTestId('migrate-dry-run') as HTMLButtonElement).disabled).toBe(false));

    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    await screen.findByText('⟦documents.migrateCanApply⟧');
    expect(posts[0]).toMatchObject({ range: 'FromMonth', fromPeriodKey: 202607, dryRun: true });
    expect(document.querySelector('[data-migrate-effective="202607"]')).not.toBeNull();
    expect(document.querySelector('[data-migrate-warn-january]')).toBeNull();
  });

  it('Q-17: ефективний січень відрізняється від X - звіт показує попередження', async () => {
    shifted = true;
    await open();

    fireEvent.click(await screen.findByRole('radio', { name: '⟦documents.migrateRangeFromMonth⟧' }));
    const field = document.querySelector('[data-migrate-range-block] .ecr-period-field input') as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '202607' } });
    await waitFor(() => expect((screen.getByTestId('migrate-dry-run') as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByTestId('migrate-dry-run'));

    await screen.findByText('⟦documents.migrateCanApply⟧');
    expect(document.querySelector('[data-migrate-warn-january]')).not.toBeNull();
    expect(document.querySelector('[data-migrate-effective="202601"]')).not.toBeNull();
  });
});
