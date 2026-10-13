import { expect, test, type Page } from '@playwright/test';

/**
 * CL-8 / ACC-OW: діалог «Перезаписати N значень» у «Перенести на нову версію шаблону» - справжній клієнт на стенді
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep versionMigrationOverwrite`), відповіді `…/migrate-version`
 * підміняються в браузері.
 *
 * ⚠ Чому підміна: база стенда не має версії шаблону з НОВОЮ комірочною формулою над уже введеним значенням, а
 * побудова такої версії через API стенда - окрема велика підготовка. Серверну поведінку прапора доводить
 * `DocumentVersionMigrationTests.GuardedOverwrite` на справжньому SQL/HTTP; тут доводиться клієнтський шлях:
 * діалог за відмовою -> «Так» -> dry-run з `acceptGuardedOverwrite: true` -> apply `async: true` з тим самим прапором;
 * «Ні» -> відмова як була, без запиту з прапором.
 *
 * ⚠ Без локального прогону (на машині автора немає стенда): перший живий прогін - CI `e2e-stand`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

interface Posted {
  readonly dryRun: boolean;
  readonly async?: boolean;
  readonly acceptGuardedOverwrite?: boolean;
}

function baseReport(overrides: Record<string, unknown>): Record<string, unknown> {
  return {
    documentId: Number(DocumentId),
    projectId: 1,
    documentCount: 1,
    fromVersionId: 1,
    fromVersion: '1.0',
    toVersionId: 2,
    toVersion: '2.0',
    mode: 'Safe',
    dryRun: true,
    applied: false,
    canApply: false,
    refusals: ['guardedWithData'],
    transferredValues: 0,
    lostValues: 0,
    guardedValues: 3,
    guardedOverwritable: 3,
    guardedOverwriteAccepted: false,
    guardedOverwrittenItems: [{ sheetCode: 'GEN', tableCode: 'T1', rowKey: 'R1', columnCode: 'C2', values: 3 }],
    guardedOverwrittenItemsTruncated: false,
    lockedSheets: 0,
    lockedSheetsCarried: 0,
    lockedSheetsAffected: 0,
    lockedSheetItemsTruncated: false,
    items: [],
    itemsTruncated: false,
    mappingsRemapped: 0,
    mappingsUnmapped: 0,
    otherProjectsOnSourceVersion: 0,
    otherDocumentsOnSourceVersion: 0,
    ...overrides,
  };
}

async function mockMigration(page: Page, posted: Posted[]): Promise<void> {
  await page.route(`**/api/v1/documents/${DocumentId}/migrate-version`, async (route) => {
    const request = route.request();
    if (request.method() === 'GET') {
      return route.fulfill({
        json: {
          projectId: 1,
          currentVersionId: 1,
          currentVersion: '1.0',
          targets: [
            { id: 2, version: '2.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: 1, publishedAt: null },
          ],
        },
      });
    }

    const body = request.postDataJSON() as Posted;
    posted.push(body);
    if (body.dryRun) {
      return route.fulfill({
        json: body.acceptGuardedOverwrite === true
          ? baseReport({ canApply: true, refusals: [], guardedOverwriteAccepted: true })
          : baseReport({}),
      });
    }

    return route.fulfill({ status: 202, json: { jobId: 'IMigrateDocumentVersionJob-e2e', documentId: Number(DocumentId) } });
  });
  await page.route('**/api/v1/jobs/IMigrateDocumentVersionJob-e2e', (route) =>
    route.fulfill({
      json: { jobId: 'IMigrateDocumentVersionJob-e2e', state: 'Running', percent: 10, message: 'Moving', error: null },
    }),
  );
}

async function openDialog(page: Page, posted: Posted[]): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  await mockMigration(page, posted);
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  await page.getByTestId('document-more').click();
  await page.locator('[data-migrate-version]').click();

  await page.getByTestId('migrate-target').click();
  await page.getByRole('option', { name: '2.0' }).click();
  await page.getByTestId('migrate-dry-run').click();
}

test.describe('versionMigrationOverwrite: діалог «Перезаписати N значень»', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );
  test.describe.configure({ timeout: 120_000 });

  test('versionMigrationOverwrite: «Так» - dry-run із прапором, потім apply async із прапором', async ({ page }) => {
    const posted: Posted[] = [];
    await openDialog(page, posted);

    const offer = page.locator('[data-migrate-overwrite-offer]');
    await expect(offer).toBeVisible({ timeout: 30_000 });
    await expect(offer).toContainText('GEN');
    await expect(page.getByTestId('migrate-apply')).toBeDisabled();

    await page.getByTestId('migrate-overwrite-yes').click();
    await expect(page.locator('[data-migrate-overwrite-accepted]')).toBeVisible();
    await expect(page.getByTestId('migrate-apply')).toBeEnabled();

    await page.getByTestId('migrate-apply').click();
    await expect(page.locator('[data-migrate-progress]')).toBeVisible();

    expect(posted).toEqual([
      { targetVersionId: 2, mode: 'Safe', dryRun: true, async: false },
      { targetVersionId: 2, mode: 'Safe', dryRun: true, async: false, acceptGuardedOverwrite: true },
      { targetVersionId: 2, mode: 'Safe', dryRun: false, async: true, acceptGuardedOverwrite: true },
    ]);
  });

  test('versionMigrationOverwrite: «Ні» - відмова лишається, запиту з прапором немає', async ({ page }) => {
    const posted: Posted[] = [];
    await openDialog(page, posted);

    await expect(page.locator('[data-migrate-overwrite-offer]')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('migrate-overwrite-no').click();

    await expect(page.locator('[data-migrate-overwrite-offer]')).toHaveCount(0);
    await expect(page.locator('[data-migrate-refused]')).toBeVisible();
    await expect(page.getByTestId('migrate-apply')).toBeDisabled();
    expect(posted.filter((p) => p.acceptGuardedOverwrite === true)).toEqual([]);
  });
});
