import { expect, test, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits PM-08c: діалоги конструктора типу дозволу на живому стенді — майстер «Add a table»,
 * «Add a column», «Add 12 month columns», «Add a check» і публікація з причиною.
 *
 * Що доводиться тут, а не у vitest: справжній модальний фокус і Escape у вкладеному діалозі,
 * `?dialog=<id>` в адресі (діалог переживає перезавантаження), результат діалогу в дереві
 * конструктора й версія типу на сервері після публікації.
 *
 * ⚠ Сценарій іде від порожнього типу, який сам заводить через API (`page.request` ділить куки з
 * вкладкою): сід типів дозволів не заводить. Опублікований тип видалити не можна (`ECR-PRM-4094`),
 * тому код кожного прогону унікальний.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "Діалоги конструктора типу дозволу"`.
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

/**
 * Чекає, поки рядок стану збереження в шапці конструктора (`SaveStatus`, `role="status"`) стане «Saved …»:
 * автозбереження чернетки йде ~1.5 с після останньої правки, а `page.goto` між кроками перезавантажує
 * застосунок і губить ще не збережене. Викликати після КОЖНОГО кроку, що змінює тип, до будь-якого `goto`.
 */
async function waitForSaved(page: Page): Promise<void> {
  const status = page.getByRole('status').filter({ hasText: /Saved|unsaved|Saving|Retry|Changed by someone/ });
  await expect(status.first(), 'рядок стану збереження не з’явився').toBeVisible({ timeout: 20_000 });
  await expect
    .poll(async () => (await status.first().innerText()).trim(), {
      message: 'автозбереження чернетки не завершилось (рядок стану не став «Saved …»)',
      timeout: 30_000,
    })
    .toMatch(/^Saved/);
}

interface PermitTypeJson {
  status: string;
  version: number;
  draftVersion: number | null;
  hasUnpublishedChanges: boolean;
  rules: { code: string; nameL10n: Record<string, string> }[];
  tables: {
    code: string;
    columns: { code: string; nameL10n?: Record<string, string> }[];
    rules: { code: string; nameL10n: Record<string, string> }[];
  }[];
}

async function getPermitType(page: Page, code: string): Promise<PermitTypeJson> {
  const response = await page.request.get(`/api/v1/permit-types/${code}`);
  return (await response.json()) as PermitTypeJson;
}

function summarize(type: PermitTypeJson): string {
  return JSON.stringify({
    status: type.status,
    version: type.version,
    draftVersion: type.draftVersion,
    hasUnpublishedChanges: type.hasUnpublishedChanges,
    typeRules: type.rules?.map((r) => r.code),
    tables: type.tables?.map((t) => ({
      code: t.code,
      columns: t.columns.map((c) => c.code),
      rules: t.rules?.map((r) => r.code),
    })),
  });
}

/** Чекає на сервері: таблиця `MonitoringPoints` з доданим стовпцем «Note» і перевірка «Volume is not negative» вже збережені. */
async function waitForServerDraft(page: Page, code: string): Promise<void> {
  await expect
    .poll(
      async () => {
        const type = await getPermitType(page, code);
        const table = type.tables?.find((t) => t.code === 'MonitoringPoints');
        const hasNote = table?.columns.some((c) => c.nameL10n?.['en'] === 'Note') ?? false;
        const rules = [...(type.rules ?? []), ...(type.tables ?? []).flatMap((t) => t.rules ?? [])];
        const hasRule = rules.some((r) => r.nameL10n['en'] === 'Volume is not negative');
        return { table: table !== undefined, hasNote, hasRule };
      },
      { message: 'чернетка на сервері не містить таблицю, стовпець «Note» і перевірку', timeout: 30_000 },
    )
    .toEqual({ table: true, hasNote: true, hasRule: true });
}

/**
 * Заголовок діалогу з діагностикою: на таймауті кидає помилку з текстом діалогу (або «no dialog»), адресою,
 * підсумком типу з API і знімком у звіті, щоб наступний лог CI пояснював себе сам.
 */
async function expectHeading(page: Page, code: string, heading: RegExp): Promise<void> {
  const dialog = page.getByRole('dialog');
  try {
    await expect(dialog.first(), 'діалог не відкрився').toBeVisible({ timeout: 30_000 });
    await expect(dialog.getByRole('heading', { name: heading }).first()).toBeVisible({ timeout: 15_000 });
  } catch (error) {
    const present = (await dialog.count()) > 0;
    const text = present ? await dialog.first().innerText().catch(() => '<не прочитано>') : 'no dialog';
    let api: string;
    try {
      api = summarize(await getPermitType(page, code));
    } catch (apiError) {
      api = `GET не вдався: ${String(apiError)}`;
    }
    await test
      .info()
      .attach('publish-dialog-failure', { body: await page.screenshot(), contentType: 'image/png' })
      .catch(() => undefined);
    throw new Error(
      `Заголовок ${String(heading)} не знайдено.\nдіалог: ${text}\nURL: ${page.url()}\nтип: ${api}\nпричина: ${String(error)}`,
    );
  }
}

test.describe('Діалоги конструктора типу дозволу', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  // Тест у кінці ПУБЛІКУЄ тип, а опублікований сервер не видаляє (`ECR-PRM-4094`): прибирання знімає лише
  // той тип, що не дійшов до публікації (тест упав раніше); готовий `E2E_Dialogs_*` лишається на стенді
  // для одноразової бази (`e2e-stand` її розгортає наново).
  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('Add table → Add column → Add check → Publish', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const code = tracked.track(`E2E_Dialogs_${Date.now()}`);
    const created = await page.request.post('/api/v1/permit-types', {
      data: { code, nameL10n: { en: 'E2E dialogs' }, category: 'Water' },
    });
    expect(created.status(), `заведення типу: ${await created.text()}`).toBe(201);

    // 1. Майстер «Add a table»: адреса тримає `?dialog=`, Escape без введеного закриває одразу.
    await page.goto(`/admin/permits/types/${code}?dialog=add-table`);
    // Саме майстер (за заголовком), а не будь-який діалог: інше вікно (підтвердження скасування, помилка) не маскується під нього.
    const wizard = page.getByRole('dialog', { name: /^Add a table/ });
    await expect(wizard).toBeVisible({ timeout: 30_000 });
    await page.keyboard.press('Escape');
    await expect(wizard).toBeHidden();
    await expect(page).not.toHaveURL(/[?&]dialog=/);

    await page.goto(`/admin/permits/types/${code}?dialog=add-table`);
    await wizard.getByLabel(/^Name \(English\)/).fill('Monitoring points');
    await wizard.getByRole('button', { name: 'Next' }).click();
    // Крок «Columns»: єдиний стовпець «Name» уже ключовий; додаємо числовий.
    await wizard.getByRole('button', { name: 'Add column' }).click();
    await wizard.getByLabel('Column 2').fill('Annual volume');
    // ⚠ Новий стовпець майстра — «Text» (`emptyDraftColumn`); перевірка `ROW.… >= 0` над текстом сервер відхиляє
    // при публікації (422, «Comparison of incompatible types»). Тому тип — «Number» (Decimal).
    await wizard.locator('#pm-nt-c1-type').click();
    await page.getByRole('option', { name: 'Number', exact: true }).click();
    await expect(wizard.locator('#pm-nt-c1-type')).toHaveValue('Number');
    await wizard.getByRole('button', { name: 'Next' }).click();
    await expect(wizard.getByText('PRM_' + code + '_MonitoringPoints')).toBeVisible();
    await wizard.getByRole('button', { name: 'Add table' }).click();
    // Спостережуваний результат, а не лише зникнення вікна: таблиця в дереві й адреса без `dialog=`.
    await expect(page.getByText('Monitoring points').first()).toBeVisible({ timeout: 15_000 });
    await expect(wizard).toBeHidden({ timeout: 15_000 });
    await expect(page).not.toHaveURL(/[?&]dialog=/, { timeout: 15_000 });
    await waitForSaved(page);

    // 2. «Add a column» у вибраній таблиці.
    await page.getByRole('button', { name: 'Add column' }).first().click();
    const column = page.getByRole('dialog');
    await column.getByLabel('Name (English)').fill('Note');
    await column.getByRole('button', { name: 'Add column' }).click();
    await expect(column).toBeHidden({ timeout: 15_000 });
    await expect(page).not.toHaveURL(/[?&]dialog=/, { timeout: 15_000 });
    await expect(page.getByText('Note').first()).toBeVisible({ timeout: 15_000 });
    await waitForSaved(page);

    // 3. «Add a check»: без виразу діалог лишається, фокус на полі.
    await page.goto(`/admin/permits/types/${code}?dialog=add-rule`);
    const rule = page.getByRole('dialog');
    await rule.getByLabel(/^What it checks/).fill('Volume is not negative');
    await rule.getByRole('button', { name: 'Add check' }).click();
    await expect(rule.getByLabel(/^Rule/)).toBeFocused();
    // Реальний код стовпця — з відповіді сервера, а не з припущення про `codeFromName`.
    const volumeCode = (await getPermitType(page, code)).tables
      .find((t) => t.code === 'MonitoringPoints')
      ?.columns.find((c) => c.nameL10n?.['en'] === 'Annual volume')?.code;
    expect(volumeCode, 'стовпець «Annual volume» відсутній у відповіді сервера').toBeTruthy();
    await rule.getByLabel(/^Rule/).fill(`ROW.${volumeCode ?? ''} >= 0`);
    await rule.getByRole('button', { name: 'Add check' }).click();
    await expect(rule).toBeHidden({ timeout: 15_000 });
    await expect(page).not.toHaveURL(/[?&]dialog=/, { timeout: 15_000 });
    await waitForSaved(page);
    await waitForServerDraft(page, code);

    // 4. Публікація: кнопка вимкнена, доки причина коротша за 10 символів.
    await page.goto(`/admin/permits/types/${code}?dialog=publish-type`);
    const publish = page.getByRole('dialog');
    await expectHeading(page, code, /Publish .* v1\?/);
    await publish.getByLabel('What changed').fill('short');
    await expect(publish.getByRole('button', { name: 'Publish' })).toBeDisabled();
    await publish.getByLabel('What changed').fill('First version of the dialogs type');
    // Публікація спершу зберігає чернетку (автозбереження → PUT), потім POST …/publish: чекаємо відповідь, а не таймер.
    const published = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        response.url().includes(`/api/v1/permit-types/${code}/publish`),
      { timeout: 60_000 },
    );
    await publish.getByRole('button', { name: 'Publish' }).click();
    const publishResponse = await published;
    // Тіло відмови (messageKey, diagnostics) — у повідомленні, щоб 422 пояснював себе в логу CI.
    const publishBody = publishResponse.ok() ? '' : await publishResponse.text().catch(() => '<тіло не прочитано>');
    expect(publishResponse.ok(), `POST …/publish: ${publishResponse.status()} ${publishBody.slice(0, 2000)}`).toBe(true);
    await expect(publish).toBeHidden({ timeout: 30_000 });
    await expect(page).not.toHaveURL(/[?&]dialog=/, { timeout: 30_000 });

    const type = (await (await page.request.get(`/api/v1/permit-types/${code}`)).json()) as {
      status: string;
      tables: { code: string; columns: { code: string }[] }[];
    };
    expect(type.status).toBe('Published');
    expect(type.tables.map((t) => t.code)).toContain('MonitoringPoints');
  });

  // P2-2 (прохід B): сервер для ніколи не опублікованого типу віддає version=1, status=Draft, і меню «…»
  // з «Delete type…» мало бути досяжним (раніше умова була `version === 0`).
  test('Видалення чернетки типу через меню «…»', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const code = tracked.track(`E2E_DeleteDraft_${Date.now()}`);
    const created = await page.request.post('/api/v1/permit-types', {
      data: { code, nameL10n: { en: 'E2E delete draft' }, category: 'Water' },
    });
    expect(created.status(), `заведення типу: ${await created.text()}`).toBe(201);
    const draft = await getPermitType(page, code);
    expect(draft.status, 'сервер віддає ніколи не опублікований тип зі status=Draft').toBe('Draft');
    expect(draft.version, 'і з version=1, а не 0').toBe(1);

    await page.goto(`/admin/permits/types/${code}`);
    const more = page.getByRole('button', { name: 'More actions' });
    await expect(more, 'меню «…» чернетки недосяжне').toBeVisible({ timeout: 30_000 });
    await more.click();
    await page.getByRole('menuitem', { name: 'Delete type…' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm).toBeVisible({ timeout: 15_000 });
    const removed = page.waitForResponse(
      (response) =>
        response.request().method() === 'DELETE' && response.url().includes(`/api/v1/permit-types/${code}`),
      { timeout: 30_000 },
    );
    await confirm.getByRole('button', { name: 'Delete type' }).click();
    expect((await removed).status()).toBe(204);

    await expect(page).toHaveURL(/\/admin\/permits\/types\/?$/, { timeout: 30_000 });
    expect((await page.request.get(`/api/v1/permit-types/${code}`)).status()).toBe(404);
    await expect(page.getByText(code)).toHaveCount(0);
  });

  // PA-U-02: той самий шлях з меню рядка ПЕРЕЛІКУ типів; для опублікованого пункт вимкнений.
  test('Видалення чернетки типу з меню рядка переліку', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const code = tracked.track(`E2E_DeleteList_${Date.now()}`);
    const created = await page.request.post('/api/v1/permit-types', {
      data: { code, nameL10n: { en: `E2E delete list ${code}` }, category: 'Water' },
    });
    expect(created.status(), `заведення типу: ${await created.text()}`).toBe(201);

    await page.goto(`/admin/permits/types?q=${encodeURIComponent(code)}`);
    const menu = page.locator(`[data-type-menu="${code}"]`);
    await expect(menu).toBeVisible({ timeout: 30_000 });
    await menu.click();
    const item = page.getByRole('menuitem', { name: 'Delete type…' });
    await expect(item).toBeEnabled();
    await item.click();

    await expect(page).toHaveURL(new RegExp(`/admin/permits/types/${code}\\?dialog=delete-type`), { timeout: 30_000 });
    const confirm = page.getByRole('dialog');
    await expect(confirm).toBeVisible({ timeout: 30_000 });
    const removed = page.waitForResponse(
      (response) =>
        response.request().method() === 'DELETE' && response.url().includes(`/api/v1/permit-types/${code}`),
      { timeout: 30_000 },
    );
    await confirm.getByRole('button', { name: 'Delete type' }).click();
    expect((await removed).status()).toBe(204);
    await expect(page).toHaveURL(/\/admin\/permits\/types\/?$/, { timeout: 30_000 });
    expect((await page.request.get(`/api/v1/permit-types/${code}`)).status()).toBe(404);
  });
});
