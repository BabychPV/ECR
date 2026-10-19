import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits PM-11: прив'язка таблиці аркуша до дозволу на живому стенді — вкладка «Permit binding» конструктора
 * версії шаблону: порожній стан → майстер (таблиця дозволу, правило, стовпці, підсумок) → прив'язка з
 * зіставленням і переглядом → видалення.
 *
 * Що доводиться тут, а не у vitest: справжній сервер приймає запит майстра (`PUT …/permit-bindings/{code}`:
 * перевірки складання типів і `read-only` стовпців), вкладка адресується `?tab=permit`, а після збереження
 * сервер віддає саме ту прив'язку, яку зібрав майстер.
 *
 * ⚠ Сценарій самодостатній: сам заводить через API (`page.request` ділить куки з вкладкою) шаблон з однією
 * динамічною таблицею, поле шапки `Facility` (потрібне правилу «за об'єктом») і опублікований тип дозволу.
 * Опублікований тип видалити не можна (`ECR-PRM-4094`), тому коди кожного прогону унікальні, а стенд —
 * одноразова база (`e2e-stand` її розгортає наново).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Permit bindings"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Відповідь API без мовчазного провалу: статус і тіло йдуть у повідомлення, щоб лог CI пояснював себе сам. */
async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function permitColumn(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code,
    nameL10n: { en: code },
    dataType: 'String',
    unitId: null,
    required: false,
    key: false,
    group: null,
    precision: null,
    scale: null,
    listScope: null,
    listRegistryCode: null,
    siblingTableCode: null,
    choices: null,
    order,
    ...over,
  };
}

function sheetColumn(order: number, header: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal: order,
    dataType: 'String',
    isRequired: false,
    isReadOnly: true,
    isHidden: false,
    precision: null,
    scale: null,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

interface Scenario {
  readonly templateId: number;
  readonly versionId: number;
  readonly tableId: number;
  readonly typeCode: string;
  readonly typeName: string;
}

/** Шаблон (чернетка) з динамічною таблицею «Sources» і опублікований тип дозволу «Points». */
async function arrange(page: Page, tracked: CreatedPermitTypes): Promise<Scenario> {
  const stamp = String(Date.now());

  const typeCode = tracked.track(`E2E_Bind_${stamp}`);
  const typeName = `E2E binding type ${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await page.request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: typeName } } }),
    'заведення типу дозволу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${typeCode}`, {
      data: {
        category: 'Water',
        nameL10n: created.nameL10n,
        rowVersion: created.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Points',
            nameL10n: { en: 'Monitoring points' },
            order: 1,
            columns: [permitColumn('Name', 1, { key: true, required: true }), permitColumn('Note', 2)],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await ok(
    await page.request.post(`/api/v1/permit-types/${typeCode}/publish`, {
      data: { reason: 'First version for the binding e2e', rowVersion: replaced.rowVersion },
    }),
    'публікація типу дозволу',
  );

  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EBind${stamp}`, nameL10n: { en: `E2E binding ${stamp}` } } }),
    'заведення шаблону',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${template.templateId}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'заведення версії шаблону',
  );
  const base = `/api/v1/template-versions/${version.versionId}`;

  await ok(
    await page.request.put(`${base}/sheets/Main`, {
      data: { nameL10n: { en: 'Main' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/Main/tables/Sources`, {
      data: { nameL10n: { en: 'Sources' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  // Стовпці з рядками з дозволу — лише для читання (сервер: `bindingNotReadOnly`); коди збігаються з полями типу.
  await ok(await page.request.put(`${base}/tables/${table.id}/columns/Name`, { data: sheetColumn(0, 'Name') }), 'стовпець Name');
  await ok(await page.request.put(`${base}/tables/${table.id}/columns/Note`, { data: sheetColumn(1, 'Note') }), 'стовпець Note');
  // Правило «за об'єктом і кінцем періоду» вимагає в шапці поле `Facility` (сервер: `bindingFacilityFieldMissing`).
  await ok(
    await page.request.put(`${base}/header-fields/Facility`, {
      data: { dataType: 'String', isRequired: false, labelL10n: { en: 'Facility' }, lookupRegistryDefId: null, ordinal: 0 },
    }),
    'поле шапки Facility',
  );

  return { templateId: template.templateId, versionId: version.versionId, tableId: table.id, typeCode, typeName };
}

/** Id стовпця таблиці версії за кодом (з відповіді структури). */
async function columnId(page: Page, s: Scenario, code: string): Promise<number> {
  const structure = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`/api/v1/template-versions/${s.versionId}/structure`),
    'структура версії',
  );
  const column = structure.sheets.flatMap((sheet) => sheet.tables).find((table) => table.id === s.tableId)?.columns.find((item) => item.code === code);
  expect(column, `стовпця ${code} немає в структурі`).toBeDefined();

  return column?.id ?? 0;
}


test.describe('Permit bindings', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  // Тип у тесті ПУБЛІКУЄТЬСЯ, а опублікований сервер не видаляє: прибирання знімає лише той, що не дійшов до
  // публікації (тест упав раніше). Готовий тип лишається на одноразовій базі стенда.
  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('майстер створює прив’язку, вкладка показує зіставлення й перегляд, видалення повертає порожній стан', async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const s = await arrange(page, tracked);

    await page.goto(`/admin/templates/${s.templateId}/versions/${s.versionId}?node=t:${s.tableId}&tab=permit`);
    await expect(page.getByTestId('binding-empty'), 'вкладка «Permit binding» не показала порожній стан').toBeVisible({ timeout: 45_000 });

    // Майстер: таблиця аркуша → таблиця дозволу → яке → стовпці → підсумок.
    await page.getByRole('button', { name: 'Bind to a permit…' }).click();
    const wizard = page.getByRole('dialog', { name: /Bind .Sources. to a permit/ });
    await expect(wizard).toBeVisible({ timeout: 30_000 });
    await expect(wizard.getByTestId('binding-sheet-table')).toHaveText('Sources');
    await wizard.getByTestId('wizard-next').click();

    await wizard.getByLabel('Permit type', { exact: true }).click();
    await page.getByRole('option', { name: s.typeName }).click();
    await expect(wizard.getByLabel('Table', { exact: true })).not.toHaveValue('');
    await wizard.getByTestId('wizard-next').click();

    // Правило за замовчуванням — «за об'єктом і кінцем періоду».
    await expect(wizard.getByRole('radio', { name: /Permit valid at period end/ })).toBeChecked();
    await wizard.getByTestId('wizard-next').click();

    // Пропонована розкладка за збігом кодів із read-only стовпцями аркуша: ключ «Name» і довідкове «Note».
    await expect(wizard.getByRole('checkbox', { name: /^Use .Name.$/ })).toBeChecked({ timeout: 30_000 });
    await expect(wizard.getByRole('checkbox', { name: /^Use .Note.$/ })).toBeChecked();
    await wizard.getByTestId('wizard-next').click();

    // Підсумок: зворотний зв'язок про руйнівні зміни і дієслово на кнопці.
    await expect(wizard.getByTestId('binding-breaking-hint')).toBeVisible();
    const saved = page.waitForResponse(
      (response) => response.request().method() === 'PUT' && response.url().includes('/permit-bindings/'),
      { timeout: 60_000 },
    );
    await wizard.getByTestId('wizard-apply').click();
    const savedResponse = await saved;
    expect(savedResponse.ok(), `PUT …/permit-bindings: ${savedResponse.status()} ${(await savedResponse.text()).slice(0, 1500)}`).toBe(true);
    await expect(wizard).toBeHidden({ timeout: 30_000 });

    // Вкладка показує прив'язку: стрілку «аркуш ← дозвіл», зіставлення і перегляд.
    const summary = page.getByTestId('binding-summary');
    await expect(summary).toContainText('Sources', { timeout: 30_000 });
    await expect(summary).toContainText(s.typeName);
    await expect(page.getByRole('row', { name: /Name.*Row key/ })).toBeVisible();
    await expect(page.getByTestId('binding-preview')).toBeVisible({ timeout: 30_000 });
    // Об'єкт не задано — перегляд каже це словами (або знаходить дозвіл); у будь-якому разі не помилка.
    await expect(page.locator('[data-testid="binding-preview-reason"], [data-testid="binding-preview-permit"]').first()).toBeVisible({
      timeout: 30_000,
    });

    // Сервер віддає ту саму прив'язку.
    const listed = await ok<{ items: { permitTypeCode: string; permitTableCode: string; mode: string; sheetTableDefId: number; columns: { role: string }[] }[] }>(
      await page.request.get(`/api/v1/template-versions/${s.versionId}/permit-bindings`),
      'перелік прив’язок',
    );
    expect(listed.items).toHaveLength(1);
    expect(listed.items[0]).toMatchObject({ permitTypeCode: s.typeCode, permitTableCode: 'Points', mode: 'Rows', sheetTableDefId: s.tableId });
    expect(listed.items[0]?.columns.map((c) => c.role).sort()).toEqual(['Info', 'RowKey']);

    // Видалення: підтвердження називає таблицю; після нього — порожній стан.
    await page.getByRole('button', { name: 'Remove binding' }).first().click();
    const confirm = page.getByRole('dialog', { name: /Remove the permit binding of .Sources./ });
    await expect(confirm).toBeVisible();
    await confirm.getByRole('button', { name: 'Remove binding' }).click();
    await expect(page.getByTestId('binding-empty')).toBeVisible({ timeout: 30_000 });

    const after = await ok<{ items: unknown[] }>(await page.request.get(`/api/v1/template-versions/${s.versionId}/permit-bindings`), 'перелік після видалення');
    expect(after.items).toHaveLength(0);
  });

  test('документ із таблицею, прив’язаною в режимі Rows: смуга «З дозволу» і «Оновити з дозволу» з підсумком', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const s = await arrange(page, tracked);
    const base = `/api/v1/template-versions/${s.versionId}`;

    // Прив'язка - тим самим запитом, що й майстер (його шлях перевіряє перший тест).
    await ok(
      await page.request.put(`${base}/permit-bindings/PB${s.tableId}_Sources`, {
        data: {
          permitTypeCode: s.typeCode,
          permitTableCode: 'Points',
          sheetTableDefId: s.tableId,
          mode: 'Rows',
          selectionRule: 'ByFacilityPeriodEnd',
          headerFieldDefId: null,
          isActive: true,
          columns: [
            { permitFieldCode: 'Name', role: 'RowKey', sheetColumnDefId: await columnId(page, s, 'Name') },
            { permitFieldCode: 'Note', role: 'Info', sheetColumnDefId: await columnId(page, s, 'Note') },
          ],
        },
      }),
      'прив’язка (API)',
    );

    // Документ: версію публікуємо, проєкт і документ заводимо на ній.
    await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: документ із прив’язкою до дозволу' } }), 'публікація версії шаблону');
    const stamp = String(Date.now());
    const project = await ok<{ id: number }>(
      await page.request.post('/api/v1/projects', {
        data: {
          code: `E2EBindPrj${stamp}`,
          nameL10n: { en: `E2E binding project ${stamp}` },
          periodKind: 'Monthly',
          timeZoneId: 'UTC',
          templateVersionId: s.versionId,
          year: new Date().getUTCFullYear(),
        },
      }),
      'проєкт',
    );
    await ok(await page.request.post(`/api/v1/projects/${project.id}/activate`), 'активація проєкту');
    const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
    const document = await ok<{ id: number }>(
      await page.request.post('/api/v1/documents', {
        data: { projectId: project.id, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: s.versionId },
      }),
      'документ',
    );
    const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
      await page.request.get(`/api/v1/projects/${project.id}/periods`),
      'календар проєкту',
    );
    const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
    expect(period, 'у проєкту немає періодів').toBeDefined();

    // Смугу вирішує `permitBinding` у відповіді …/tables (без Template.View і без додаткових запитів).
    await page.goto(`/documents/${document.id}?periodKey=${String(period?.periodKey)}`);
    const strip = page.getByTestId('permit-refresh-strip');
    await expect(strip, 'смуги «З дозволу» немає: сервер не віддав permitBinding').toBeVisible({ timeout: 60_000 });
    await expect(strip.getByTestId('from-permit-badge')).toBeVisible();

    await strip.getByRole('button', { name: 'Refresh from permit' }).click();
    // Об'єкт документа порожній - дозволу немає, тож рядків не додано; підсумок (або попередження) все одно показується.
    await expect(page.getByTestId('permit-refresh-result'), 'підсумок «Оновити з дозволу» не з’явився').toBeVisible({ timeout: 60_000 });
  });});
