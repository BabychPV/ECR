import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits (групи типів) - РЕАЛЬНИЙ e2e, без підміни відповідей: сервер груп (PRMG01 + сервіс) і клієнт разом.
 *
 * Сценарій: три опубліковані типи з дозволами -> група через екран Groups (POST) -> у чернетці версії шаблону поле
 * шапки Lookup обирає групу в Select (група «Permits», опис другим рядком) -> збереження -> публікація, проєкт, документ ->
 * піклер шапки пропонує дозволи ВСІХ типів складу у вікні проєкту підписом «номер — тип»; вибір зберігається і переживає
 * перезавантаження. Групу створюють і вибирають N/5.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `-Grep "Permit groups real"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code, nameL10n: { en: code }, dataType: 'String', unitId: null, required: false, key: false, group: null, precision: null,
    scale: null, listScope: null, listRegistryCode: null, siblingTableCode: null, choices: null, order, ...over,
  };
}

interface PermitType {
  readonly code: string;
  readonly name: string;
  readonly numbers: { readonly a: string; readonly b: string; readonly draft: string; readonly expired: string };
}

async function createPermit(page: Page, typeCode: string, number: string, validFrom: string, validTo: string, activate: boolean): Promise<void> {
  const created = await ok<{ id: number }>(
    await page.request.post('/api/v1/permits', {
      data: {
        typeCode, number, location: 'E2E facility', issueDate: validFrom, validFrom, validTo,
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    `дозвіл ${number}`,
  );
  if (activate) await ok(await page.request.post(`/api/v1/permits/${String(created.id)}/activate`), `активація ${number}`);
}

/** Опублікований тип дозволу з двома чинними, одним чернетковим і одним закінченим дозволом. */
async function publishedType(page: Page, tracked: CreatedPermitTypes, stamp: string, tag: string): Promise<PermitType> {
  const code = tracked.track(`E2E_PGR_${tag}_${stamp}`);
  const name = `E2E group type ${tag} ${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await page.request.post('/api/v1/permit-types', { data: { code, category: 'Water', nameL10n: { en: name } } }),
    `тип ${tag}`,
  );
  const replaced = await ok<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Water', nameL10n: created.nameL10n, rowVersion: created.rowVersion, rules: [],
        tables: [{ code: 'Limits', nameL10n: { en: 'Limits' }, order: 1, columns: [column('Name', 1, { key: true, required: true })], rules: [] }],
      },
    }),
    `чернетка типу ${tag}`,
  );
  await ok(await page.request.post(`/api/v1/permit-types/${code}/publish`, { data: { reason: 'E2E groups', rowVersion: replaced.rowVersion } }), `публікація ${tag}`);

  const numbers = { a: `E2E-PGR-${tag}A-${stamp}`, b: `E2E-PGR-${tag}B-${stamp}`, draft: `E2E-PGR-${tag}D-${stamp}`, expired: `E2E-PGR-${tag}O-${stamp}` };
  await createPermit(page, code, numbers.a, '2026-01-10', '2027-01-10', true);
  await createPermit(page, code, numbers.b, '2026-02-01', '2026-12-01', true);
  await createPermit(page, code, numbers.draft, '2026-01-10', '2027-01-10', false);
  await createPermit(page, code, numbers.expired, '2024-01-01', '2025-01-01', true);

  return { code, name, numbers };
}

/** Створення групи ЕКРАНОМ Groups (реальний POST) з усіх переданих типів. */
async function createGroupViaUi(page: Page, code: string, name: string, description: string, types: readonly PermitType[]): Promise<void> {
  await page.goto('/admin/permits/groups');
  await expect(page.getByRole('heading', { name: 'Groups' }).first()).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'New group' }).first().click();
  const drawer = page.getByRole('dialog');
  await expect(drawer).toBeVisible();
  await drawer.getByLabel(/^Code/).fill(code);
  await drawer.getByRole('textbox', { name: /^Name · English/ }).fill(name);
  await drawer.getByRole('textbox', { name: /^Description · English/ }).fill(description);
  const picker = drawer.getByRole('textbox', { name: 'Permit types in the group' });
  for (const type of types) {
    await picker.click();
    await page.getByRole('option', { name: type.name, exact: true }).click();
  }
  // Випадний список складу лишається відкритим (обрані опції позначені): закрити його кліком по заголовку шторки.
  await drawer.getByText('New permit group').click();
  await expect(page.getByRole('option').first()).toBeHidden();
  const created = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().endsWith('/api/v1/permit-groups'));
  await drawer.getByRole('button', { name: 'Create group' }).click();
  const response = await created;
  expect(response.status(), `POST permit-groups: ${response.status()} ${await response.text()}`).toBe(201);
  await expect(drawer).toBeHidden({ timeout: 15_000 });
  await expect(page.getByRole('row', { name: new RegExp(name) })).toBeVisible();
}

test.describe('Permit groups real', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');
  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('група з трьох типів: екран -> Select у шаблоні -> піклер у документі «номер — тип» (N/5)', async ({ page }) => {
    test.setTimeout(900_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const stamp = String(Date.now());
    const types = [await publishedType(page, tracked, stamp, 'W'), await publishedType(page, tracked, stamp, 'D'), await publishedType(page, tracked, stamp, 'S')];
    const outsider = await publishedType(page, tracked, stamp, 'X');
    const groupCode = `E2E_PGR_${stamp}`;
    const groupName = `E2E water group ${stamp}`;
    const description = `All e2e water permits ${stamp}`;
    await createGroupViaUi(page, groupCode, groupName, description, types);

    // Реальний GET: група віддана з іменем, описом і складом у заданому порядку.
    const read = await ok<{ nameL10n: Record<string, string>; descriptionL10n: Record<string, string>; types: { code: string }[]; registryCode: string }>(
      await page.request.get(`/api/v1/permit-groups/${groupCode}`),
      'GET групи',
    );
    expect(read.nameL10n['en']).toBe(groupName);
    expect(read.descriptionL10n['en']).toBe(description);
    expect(read.types.map((type) => type.code)).toEqual(types.map((type) => type.code));
    expect(read.registryCode).toBe(`PRMG_${groupCode}`);
    // Цілі Lookup віддають групу з kind=PermitGroup і описом; звичайний перелік її не віддає.
    const targets = await ok<{ code: string; kind?: string; descriptionL10n?: { values?: Record<string, string> } | null }[]>(
      await page.request.get('/api/v1/registries?includePermitHeaders=true'),
      'цілі Lookup',
    );
    const target = targets.find((registry) => registry.code === `PRMG_${groupCode}`);
    expect(target?.kind, 'kind групи в цілях Lookup').toBe('PermitGroup');
    expect(target?.descriptionL10n?.values?.['en'], 'опис групи в цілях Lookup').toBe(description);
    const plain = await ok<{ code: string }[]>(await page.request.get('/api/v1/registries'), 'звичайний перелік');
    expect(plain.some((registry) => registry.code.startsWith('PRMG_')), 'PRMG_ не в «Довідниках»').toBe(false);

    // Шаблон: каркас через API, поле шапки Lookup - через Select у чернетці версії (N/5 вибору групи).
    const template = await ok<{ templateId: number }>(
      await page.request.post('/api/v1/templates', { data: { code: `E2EPgr${stamp}`, nameL10n: { en: `E2E groups ${stamp}` } } }),
      'шаблон',
    );
    const version = await ok<{ versionId: number }>(
      await page.request.post(`/api/v1/templates/${template.templateId}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
      'версія',
    );
    const base = `/api/v1/template-versions/${version.versionId}`;
    await ok(await page.request.put(`${base}/sheets/Main`, { data: { nameL10n: { en: 'Main' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/Main/tables/Data`, { data: { nameL10n: { en: 'Data' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
      'таблиця',
    );
    await ok(
      await page.request.put(`${base}/tables/${table.id}/columns/Note`, {
        data: {
          headerL10n: { en: 'Note' }, ordinal: 0, dataType: 'String', isRequired: false, isReadOnly: false, isHidden: false, precision: null,
          scale: null, defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId: null, styleId: null, unitId: null,
        },
      }),
      'стовпець',
    );

    for (let run = 1; run <= 5; run += 1) {
      await page.goto(`/admin/templates/${String(template.templateId)}/versions/${String(version.versionId)}`);
      // Поля шапки - у вузлі версії (за наявності таблиці сторінка відкривається на ній).
      await page.getByRole('treeitem', { name: /^Version / }).click();
      await (
        run === 1
          ? page.getByRole('button', { name: 'Add header field' }).first()
          : page.getByRole('row', { name: /Permit number \(PermitNo\)/ }).getByRole('button', { name: 'Edit' })
      ).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      if (run === 1) {
        await dialog.getByLabel('Code', { exact: true }).fill('PermitNo');
        await dialog.getByLabel(/^Label/).first().fill('Permit number');
        await dialog.getByRole('textbox', { name: 'Data type' }).click();
        await page.getByRole('option', { name: 'Lookup' }).click();
      }
      const registry = dialog.getByRole('textbox', { name: 'Registry' });
      // Повторне відкриття: вибрана група пережила збереження і показана (не порожнє поле).
      if (run > 1) await expect(registry, `прогін ${run}: збережена група`).toHaveValue(new RegExp(`${groupName} \\(PRMG_${groupCode}\\)`));
      await registry.click();
      const labels = await page.evaluate(() => Array.from(document.querySelectorAll('[class*="groupLabel"]')).map((node) => node.textContent));
      expect(labels[0], `прогін ${run}: група Permits перша ${JSON.stringify(labels)}`).toBe('Permits');
      const option = page.getByRole('option', { name: new RegExp(`${groupName} \\(PRMG_${groupCode}\\)`) });
      await expect(option, `прогін ${run}: група в Select`).toBeVisible({ timeout: 30_000 });
      await expect(option.locator('[data-lookup-target-description]'), `прогін ${run}: опис групи`).toHaveText(description);
      // Тип з групи НЕ розпізнається як довідник: PRMG_ лише в «Permits».
      if (run === 1) {
        await option.click();
        await expect(registry).toHaveValue(new RegExp(`PRMG_${groupCode}`));
        await dialog.getByRole('button', { name: 'Save field' }).click();
      } else {
        // Група вже вибрана: закриваємо без змін (клік по вибраній опції зняв би вибір).
        await dialog.getByText('Label', { exact: false }).first().click();
        await dialog.getByRole('button', { name: 'Cancel' }).click();
      }
      await expect(dialog).toBeHidden({ timeout: 30_000 });
    }

    // Публікація версії -> проєкт -> документ.
    await ok(await page.request.post(`${base}/publish`, { data: { reason: 'E2E groups' } }), 'публікація версії');
    const structure = await ok<{ sheets: { id: number; code: string }[] }>(await page.request.get(`${base}/structure`), 'структура');
    const sheetId = structure.sheets.find((sheet) => sheet.code === 'Main')?.id ?? 0;
    const policies = await ok<{ id: number }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
    const project = await ok<{ projectId: number }>(
      await page.request.post('/api/v1/projects', {
        data: {
          code: `E2EPGR${stamp}`, nameL10n: { en: `E2E groups project ${stamp}` }, periodKind: 'Monthly', periodPolicyId: policies[0]?.id ?? null,
          templateVersionId: version.versionId, timeZoneId: 'Asia/Atyrau', year: 2026,
        },
      }),
      'проєкт',
    );
    const doc = await ok<{ documentId: number }>(
      await page.request.post('/api/v1/documents', { data: { projectId: project.projectId, sheetDefIds: [sheetId], templateVersionId: version.versionId } }),
      'документ',
    );
    const calendar = await ok<{ periods: { periodKey: number }[] }>(await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`), 'календар');
    const period = String(calendar.periods[0]?.periodKey ?? '');

    const open = async (): Promise<void> => {
      await page.goto(`/documents/${String(doc.documentId)}?periodKey=${period}`);
      const toggle = page.getByTestId('document-header-toggle');
      await expect(toggle).toBeVisible({ timeout: 60_000 });
      if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
    };
    await open();
    const field = page.getByRole('textbox', { name: 'Permit number' });
    await expect(field).toBeEnabled({ timeout: 30_000 });
    await field.click();
    const options = page.getByRole('listbox', { name: 'Permit number' }).getByRole('option');
    await expect(options.first()).toBeVisible({ timeout: 15_000 });
    const shown = (await options.allTextContents()).map((text) => text.trim()).sort();
    const expected = types.flatMap((type) => [`${type.numbers.a} — ${type.name}`, `${type.numbers.b} — ${type.name}`]).sort();
    expect(shown, 'піклер групи: чинні дозволи всіх типів складу, «номер — тип»').toEqual(expected);
    for (const type of types) {
      for (const hidden of [type.numbers.draft, type.numbers.expired]) {
        expect(shown.some((text) => text.startsWith(hidden)), `дозвіл ${hidden} не повинен пропонуватись`).toBe(false);
      }
    }
    await page.keyboard.press('Escape');

    // N/5: вибір (по колу через типи) -> Save -> перезавантаження -> значення на місці.
    const picks = [types[0]!.numbers.a, types[1]!.numbers.b, types[2]!.numbers.a, types[0]!.numbers.b, types[1]!.numbers.a];
    for (const [index, number] of picks.entries()) {
      await open();
      const input = page.getByRole('textbox', { name: 'Permit number' });
      await expect(input).toBeEnabled({ timeout: 30_000 });
      await input.click();
      await page.getByRole('option', { name: new RegExp(`^${number} — `) }).click();
      await expect(input).toHaveValue(new RegExp(`^${number} — `));
      const saved = page.waitForResponse((response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${String(doc.documentId)}/header`));
      await page.getByTestId('document-header-panel').getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/ }).click();
      const response = await saved;
      expect(response.ok(), `повтор ${String(index + 1)}/5: збереження шапки: ${response.status()} ${await response.text()}`).toBe(true);
      await page.reload();
      await open();
      await expect(page.getByRole('textbox', { name: 'Permit number' }), `повтор ${String(index + 1)}/5: значення не пережило перезавантаження`).toHaveValue(
        new RegExp(`^${number} — `),
        { timeout: 20_000 },
      );
    }

    // S18/без оракула: запис ІНШОГО типу (поза групою) у цьому ж полі - та сама відмова 422, що й для неіснуючого запису.
    const outsiderEntries = await ok<{ id: number; display: string }[]>(
      await page.request.get(`/api/v1/registries/PRM_${outsider.code}/entries?documentId=${String(doc.documentId)}`),
      'записи типу поза групою',
    );
    const outsiderId = outsiderEntries.find((entry) => entry.display === outsider.numbers.a)?.id;
    expect(outsiderId, 'дозвіл типу поза групою').toBeDefined();
    const groupEntries = await ok<{ id: number; display: string }[]>(
      await page.request.get(`/api/v1/registries/PRMG_${groupCode}/entries?documentId=${String(doc.documentId)}`),
      'піклер групи',
    );
    const memberId = groupEntries.find((entry) => entry.display === types[0]!.numbers.a)?.id;
    expect(memberId, 'дозвіл члена групи').toBeDefined();
    await open();
    const probe = page.getByRole('textbox', { name: 'Permit number' });
    await expect(probe).toBeEnabled({ timeout: 30_000 });
    await probe.click();
    await page.getByRole('option', { name: new RegExp(`^${types[2]!.numbers.b} — `) }).click();
    // Підміна id у запиті UI на запис іншого типу (поза групою): сервер має відмовити 422, як і для неіснуючого запису.
    const chosenId = groupEntries.find((entry) => entry.display === types[2]!.numbers.b)?.id;
    const headerUrl = `**/api/v1/documents/${String(doc.documentId)}/header`;
    await page.route(headerUrl, async (route) => {
      if (route.request().method() !== 'PATCH') return route.continue();
      const body = route.request().postData() ?? '';
      expect(body, 'тіло PATCH містить id вибраного запису').toContain(String(chosenId));

      return route.continue({ postData: body.split(String(chosenId)).join(String(outsiderId)) });
    });
    const forgedDone = page.waitForResponse((response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${String(doc.documentId)}/header`));
    await page.getByTestId('document-header-panel').getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/ }).click();
    const forgedResponse = await forgedDone;
    console.log(`PROBE other-type: ${String(forgedResponse.status())} ${await forgedResponse.text()}`);
    expect(forgedResponse.status(), 'запис іншого типу поза групою відхиляється 422').toBe(422);
    await page.unroute(headerUrl);
    expect(memberId, 'дозвіл члена групи').toBeDefined();
  });
});
