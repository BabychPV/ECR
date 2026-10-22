import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { VersionMigrationDialog } from '@/features/documents/VersionMigrationDialog';
import { BackupConfirmationThreshold } from '@/features/documents/versionMigrationApi';
import { findViolations, describe as describeViolations } from '@/test/a11y';
import { testTheme } from '@/test/render';

/**
 * KI-2: підтвердження резервної копії в діалозі «Перенести на нову версію шаблону».
 *
 * Сервер (`MigrateDocumentVersionHandler.BackupConfirmationThreshold` = 500 000) без `backupConfirmation` відмовляє
 * великий перенос (`migrateBackupRequired`; у фоні - задача Failed). Діалог показує поле за звітом сухого прогону
 * (`transferredValues >= BackupConfirmationThreshold`), тримає «Перенести» вимкненою без значення й передає його в apply.
 */
let transferred = 0;
const posts: Record<string, unknown>[] = [];

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
    transferredValues: transferred,
    lostValues: 0,
    guardedValues: 0,
    lockedSheets: 0,
    lockedSheetItemsTruncated: false,
    items: [],
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

      if (url.includes('/migrate-version') && init?.method === 'POST') {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        posts.push(body);

        return body['dryRun'] === true ? json(report()) : json({ jobId: 'job-1' }, 202);
      }

      return json({
        projectId: 7,
        currentVersionId: 1,
        currentVersion: '1.0',
        targets: [{ id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null }],
      });
    }),
  );
}

async function openAndDryRun(): Promise<HTMLElement> {
  stubServer();
  const view = render(
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

  return view.baseElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
  posts.length = 0;
});

describe('VersionMigrationDialog: підтвердження резервної копії (KI-2)', () => {
  it('нижче порогу поля немає, а «Перенести» доступна', async () => {
    transferred = BackupConfirmationThreshold - 1;
    await openAndDryRun();

    expect(screen.queryByTestId('migrate-backup-confirmation')).toBeNull();
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(false);
  });

  it('від порогу поле з\'являється, обов\'язкове, а «Перенести» вимкнена доти, доки його не заповнено', async () => {
    transferred = BackupConfirmationThreshold;
    await openAndDryRun();

    const field = screen.getByTestId('migrate-backup-confirmation') as HTMLInputElement;
    expect(field.required).toBe(true);
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(true);

    // Порожнє після виходу з поля: помилка повʼязана з полем (aria-invalid + aria-describedby), а не лише колір.
    fireEvent.focus(field);
    fireEvent.blur(field);
    await screen.findByText('⟦documents.migrateBackupMissing⟧');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    const errorId = field.getAttribute('aria-describedby')?.split(' ').find((id) => document.getElementById(id)?.textContent === '⟦documents.migrateBackupMissing⟧');
    expect(errorId).toBeDefined();

    // Самі пробіли - теж «порожньо».
    fireEvent.change(field, { target: { value: '   ' } });
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(field, { target: { value: 'D:\\Backup\\ecr-2026-10-22.bak' } });
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(false);
  });

  it('значення потрапляє в запит apply (async) як backupConfirmation, у сухий прогін - ні', async () => {
    transferred = 600_000;
    await openAndDryRun();

    fireEvent.change(screen.getByTestId('migrate-backup-confirmation'), { target: { value: '  D:\\Backup\\ecr.bak 2026-10-22 09:00  ' } });
    fireEvent.click(screen.getByTestId('migrate-apply'));

    await waitFor(() => expect(posts.some((p) => p['dryRun'] === false)).toBe(true));
    const dry = posts.find((p) => p['dryRun'] === true);
    const applied = posts.find((p) => p['dryRun'] === false);
    expect(dry).not.toHaveProperty('backupConfirmation');
    expect(applied).toMatchObject({ async: true, backupConfirmation: 'D:\\Backup\\ecr.bak 2026-10-22 09:00' });
  });

  it('нижче порогу apply іде без backupConfirmation', async () => {
    transferred = 10;
    await openAndDryRun();

    fireEvent.click(screen.getByTestId('migrate-apply'));

    await waitFor(() => expect(posts.some((p) => p['dryRun'] === false)).toBe(true));
    expect(posts.find((p) => p['dryRun'] === false)).not.toHaveProperty('backupConfirmation');
  });

  it('поле отримує фокус, коли з\'являється після сухого прогону', async () => {
    transferred = BackupConfirmationThreshold;
    await openAndDryRun();

    expect(document.activeElement).toBe(screen.getByTestId('migrate-backup-confirmation'));
  });

  it('значення, введене для одного вибору, не потрапляє в запит після зміни режиму', async () => {
    transferred = BackupConfirmationThreshold;
    await openAndDryRun();

    fireEvent.change(screen.getByTestId('migrate-backup-confirmation'), { target: { value: 'D:\\Backup\\old.bak' } });
    fireEvent.click(screen.getByText('⟦documents.migrateModePresentation⟧'));

    // Звіт скинуто разом з вибором - поля немає; новий сухий прогін показує його порожнім.
    await waitFor(() => expect(screen.queryByTestId('migrate-backup-confirmation')).toBeNull());
    fireEvent.click(screen.getByTestId('migrate-dry-run'));
    const field = (await screen.findByTestId('migrate-backup-confirmation')) as HTMLInputElement;
    expect(field.value).toBe('');
    expect((screen.getByTestId('migrate-apply') as HTMLButtonElement).disabled).toBe(true);
  });

  it('a11y: поле має доступне ім\'я й опис, критичних порушень axe немає', async () => {
    transferred = BackupConfirmationThreshold;
    const root = await openAndDryRun();

    const field = screen.getByRole('textbox', { name: /⟦documents\.migrateBackupLabel⟧/ });
    expect(field.getAttribute('aria-describedby')).not.toBeNull();

    // Перевіряється поле з підказкою й помилкою; закривна кнопка Modal без імені - окремий наявний борг діалогу (поза KI-2).
    const violations = await findViolations(field.closest('.mantine-InputWrapper-root') ?? root);
    expect(violations, describeViolations(violations)).toEqual([]);
  });
});
