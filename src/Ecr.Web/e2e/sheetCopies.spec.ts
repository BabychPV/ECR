import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * Копії аркуша в документі проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep sheetCopies`).
 *
 * Сценарій: шаблон з аркушем `isCopyable = true` (заводиться через API: сід копійованих аркушів не містить) ->
 * документ -> «Add copy» -> вкладка «<Назва> 2» -> «Rename» групи -> «Delete copy» з підтвердженням.
 * У оригіналу «Delete copy» немає.
 *
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 180_000 });

const SheetName = 'E2E copy sheet';
const RenamedGroup = 'E2E renamed group';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Документ з одним копійованим аркушем (`isCopyable`) і однією таблицею. */
async function createCopyableDocument(page: Page): Promise<{ documentId: number; periodKey: number }> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ECopy${stamp}`, nameL10n: { en: `E2E copy ${stamp}` } } }),
    'заведення шаблону',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'заведення версії шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  await ok(
    await page.request.put(`${base}/sheets/COPYSHEET`, {
      data: { nameL10n: { en: SheetName }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null, isCopyable: true },
    }),
    'копійований аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/COPYSHEET/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця аркуша',
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/columns/C1`, {
      data: {
        headerL10n: { en: 'C1' },
        ordinal: 0,
        dataType: 'String',
        isRequired: false,
        isReadOnly: false,
        isHidden: false,
        precision: null,
        scale: null,
        defaultValue: null,
        displayFormat: null,
        lookupFilter: null,
        lookupRegistryDefId: null,
        styleId: null,
        unitId: null,
      },
    }),
    'стовпець',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: документ із копійованим аркушем' } }), 'публікація версії');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ECopyPrj${stamp}`,
        nameL10n: { en: `E2E copy project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкту немає періодів').toBeDefined();

  return { documentId: document.documentId, periodKey: period?.periodKey ?? 0 };
}

interface ChatPage {
  messages: { body: string | null }[];
}

/** TC02: чат копії ізольований від чату оригіналу; діалог видалення копії називає кількість її повідомлень. */
test('копія аркуша: чат копії не видно в чаті оригіналу; діалог видалення показує кількість повідомлень', async ({ page }) => {
  await signIn(page);
  const doc = await createCopyableDocument(page);
  const documentUrl = `/api/v1/documents/${String(doc.documentId)}`;

  const before = await ok<{ sheetDefId: number; tableCode: string }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(doc.periodKey)}`),
    'таблиці документа',
  );
  const first = before[0];
  expect(first, 'у документа немає таблиць').toBeDefined();
  await ok(await page.request.post(`${documentUrl}/sheets/${String(first?.sheetDefId)}/copies`), 'копія аркуша');

  const tables = await ok<{ tableCode: string; sheetCopyId?: number | null }[]>(
    await page.request.get(`${documentUrl}/tables?periodKey=${String(doc.periodKey)}`),
    'таблиці після копії',
  );
  const copyTable = tables.find((table) => table.sheetCopyId !== undefined && table.sheetCopyId !== null);
  expect(copyTable, 'у відповіді немає таблиці копії').toBeDefined();
  const copyId = copyTable?.sheetCopyId ?? 0;
  const messages = `${documentUrl}/tables/${copyTable?.tableCode ?? ''}/messages`;

  await ok(
    await page.request.post(`${messages}?sheetCopyId=${String(copyId)}`, {
      data: { body: 'e2e copy chat message', periodKey: doc.periodKey, clientMessageId: crypto.randomUUID(), mentionUserIds: [] },
    }),
    'повідомлення в чаті копії',
  );

  const inCopy = await ok<ChatPage>(await page.request.get(`${messages}?sheetCopyId=${String(copyId)}&limit=50`), 'чат копії');
  const inOriginal = await ok<ChatPage>(await page.request.get(`${messages}?limit=50`), 'чат оригінала');
  expect(inCopy.messages.map((message) => message.body)).toEqual(['e2e copy chat message']);
  expect(inOriginal.messages, 'повідомлення копії потрапило в чат оригінала').toEqual([]);

  // Діалог видалення копії: число повідомлень видно до підтвердження.
  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}&sheet=COPYSHEET~${String(copyId)}`);
  await page.getByTestId('sheet-copy-delete').click();
  await expect(page.getByRole('dialog')).toContainText(/Chat messages of this copy that will be deleted with it: 1/, { timeout: 30_000 });
});

test('копія аркуша: додати -> вкладка «Назва 2» -> перейменувати групу -> видалити з підтвердженням', async ({ page }) => {
  await signIn(page);
  const doc = await createCopyableDocument(page);
  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);

  // Оригінал: одна вкладка без номера, «Add copy» є, «Delete copy» немає.
  const original = page.getByRole('tab', { name: new RegExp(SheetName) });
  await expect(original, 'вкладка копійованого аркуша не з’явилась').toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('sheet-copy-add')).toBeVisible();
  await expect(page.getByTestId('sheet-copy-delete')).toHaveCount(0);
  await expect(page.getByRole('tab')).toHaveCount(1);

  // Додати копію: з’являється «<Назва> 2» і стає активною; на копії «Delete copy» є.
  await page.getByTestId('sheet-copy-add').click();
  const copyTab = page.getByRole('tab', { name: new RegExp(`${SheetName} 2`) });
  await expect(copyTab).toBeVisible({ timeout: 30_000 });
  await expect(copyTab).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('tab', { name: new RegExp(`${SheetName} 1`) })).toBeVisible();
  await expect(page.getByTestId('sheet-copy-delete')).toBeVisible();

  // Перейменувати групу: нова назва — на обох вкладках.
  await page.getByTestId('sheet-copy-rename').click();
  const dialog = page.getByRole('dialog');
  const nameInput = dialog.getByRole('textbox', { name: /English/ });
  await nameInput.fill(RenamedGroup);
  await page.getByTestId('sheet-copy-rename-save').click();
  await expect(page.getByRole('tab', { name: new RegExp(`${RenamedGroup} 1`) })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('tab', { name: new RegExp(`${RenamedGroup} 2`) })).toBeVisible();

  // Видалити копію: діалог підтвердження -> вкладка зникає, активним стає оригінал, «Delete copy» зникає.
  await page.getByTestId('sheet-copy-delete').click();
  await page.getByTestId('confirm-verb').click();
  await expect(page.getByRole('tab', { name: new RegExp(`${RenamedGroup} 2`) })).toHaveCount(0, { timeout: 30_000 });
  await expect(page.getByRole('tab')).toHaveCount(1);
  await expect(page.getByRole('tab', { name: new RegExp(RenamedGroup) })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('sheet-copy-delete')).toHaveCount(0);
});
