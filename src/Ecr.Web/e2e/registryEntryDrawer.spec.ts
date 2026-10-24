import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';

/**
 * Шторка запису на сторінці даних довідника (`/admin/registries/:code/entries`) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: `EntryDrawer` — окремий лінивий чанк (`D-132`), а стан шторки живе в
 * адресі (`?panel=entry-<id>`, `entryPanelId` + `useUrlState('panel')`). Регресію лінивого завантаження чи стану
 * в адресі vitest не бачить (там модуль підставлений, адреса — пам'ять). Тут:
 *   1. запис відкривається з сітки → шторка з даними САМЕ цього запису, адреса несе `panel=entry-<id>`;
 *   2. `Escape` → `panel` зникає з адреси, фокус повертається на відкривач (як `registriesList.spec.ts`);
 *   3. перезавантаження за адресою з `panel` → шторка відкривається одразу з тим самим записом (глибоке посилання);
 *      кнопка закриття знімає `panel`;
 *   4. правка поля через «Edit» у шторці → нове значення в шторці й після перезавантаження; потім значення
 *      повертається.
 *
 * ⚠ Дані — свої: свіжий стенд довідників не має (сід їх не заводить — це дані). `beforeAll` заводить довідник
 * `E2E_EDR_<час>` із текстовим полем `NOTE` і один запис тим самим API, що й інтерфейс; `afterAll` видаляє запис.
 * Видалення довідника API не має (див. `registryDesigner.spec.ts`), тож залишок — порожній `E2E_EDR_*`.
 * Від порядку файлів спека не залежить.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається — як `approvalQueueLive.spec.ts`.
 *
 * Запуск: workflow `e2e-stand` з `grep="Шторка запису довідника"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

const FieldCode = 'NOTE';
const FieldName = 'Note';

interface Seed {
  readonly registry: string;
  readonly registryDefId: number;
  readonly entryId: number;
  readonly entryCode: string;
  readonly display: string;
  readonly note: string;
}

interface RowsPage {
  items: { id: number; code: string; display: string; values: Record<string, { value: string | null; display: string | null } | undefined> }[];
}

/** Сесія адміністратора для підготовки даних: окремий контекст + вхід через API (як `approvalQueueLive`). */
async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: Admin.userName, password: Admin.password },
  });
  expect(login.ok(), `вхід ${Admin.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);
  return context;
}

/** Вхід через сторінку входу — як людина (`registriesList.spec.ts`). */
async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function readRow(request: APIRequestContext, seed: Seed): Promise<RowsPage['items'][number]> {
  const response = await request.get(`/api/v1/registries/${seed.registry}/rows?id=${String(seed.entryId)}&limit=1`);
  expect(response.ok(), `рядок запису: ${String(response.status())} ${await response.text()}`).toBe(true);
  const row = ((await response.json()) as RowsPage).items.find((item) => item.id === seed.entryId);
  expect(row, `запис ${String(seed.entryId)} не прочитався`).toBeDefined();
  return row as RowsPage['items'][number];
}

/** Значення поля запису — як його віддає сервер (`display ?? value`, так само показує шторка). */
function noteOf(row: RowsPage['items'][number]): string {
  const value = row.values[FieldCode];
  return value?.display ?? value?.value ?? '';
}

/** Пише значення `NOTE` наявного запису тим самим `POST …/entries`, що й форма «Edit». */
async function writeNote(request: APIRequestContext, seed: Seed, note: string): Promise<void> {
  const response = await request.post(`/api/v1/registries/${seed.registry}/entries`, {
    data: {
      id: seed.entryId,
      registryDefId: seed.registryDefId,
      code: seed.entryCode,
      // Назва зливається з наявною: порожній набір мов її не змінює.
      display: { values: {} },
      parentEntryId: null,
      values: { [FieldCode]: note },
    },
  });
  expect(response.status(), `запис значення ${FieldCode}: ${await response.text()}`).toBe(200);
}

/** Шторка саме цього запису: `data-panel` стоїть на корені `Drawer`, діалог — усередині. */
function drawerOf(page: Page, seed: Seed): Locator {
  return page.locator(`[data-panel="entry-${String(seed.entryId)}"]`).getByRole('dialog');
}

/** Значення пари «підпис → значення» у `KeyValue` шторки (`<dt>`/`<dd>` в одній обгортці). */
function pairValue(drawer: Locator, label: string): Locator {
  return drawer
    .locator('[data-key-value-item]')
    .filter({ has: drawer.page().locator(`dt:text-is("${label}")`) })
    .locator('dd')
    .first();
}

function panelUrl(seed: Seed): RegExp {
  return new RegExp(`[?&]panel=entry-${String(seed.entryId)}(&|$)`);
}

test.describe('Шторка запису довідника: адреса, глибоке посилання, фокус, правка', () => {
  test.describe.configure({ timeout: 180_000 });

  let seed: Seed | null = null;

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');
    }

    const admin = await signedIn(browser);
    try {
      const stamp = String(Date.now());
      const registry = `E2E_EDR_${stamp}`;

      const created = await admin.request.post('/api/v1/registries', {
        data: { code: registry, nameL10n: { en: `E2E entry drawer ${stamp}` }, isTemporal: false },
      });
      expect(created.status(), `створення довідника: ${await created.text()}`).toBe(201);

      // Текстове поле — щоб шторка показувала значення поля запису, а не лише код і назву.
      const definitionResponse = await admin.request.get(`/api/v1/registries/${registry}/definition`);
      expect(definitionResponse.ok(), `опис довідника: ${await definitionResponse.text()}`).toBe(true);
      const definition = (await definitionResponse.json()) as { id: number; definitionVersion: number; codeMode?: string | null };

      const saved = await admin.request.put(`/api/v1/registries/${registry}/definition`, {
        headers: { 'If-Match': `"${String(definition.definitionVersion)}"` },
        data: {
          fields: [
            {
              id: null,
              code: FieldCode,
              nameL10n: { values: { en: FieldName } },
              dataType: 'String',
              ordinal: 1,
              isRequired: false,
              isKey: false,
              lookupRegistryDefId: null,
              unitId: null,
            },
          ],
          rules: [],
          reason: 'e2e: registryEntryDrawer — поле для шторки запису',
        },
      });
      expect(saved.status(), `поле ${FieldCode}: ${await saved.text()}`).toBe(200);

      const display = `E2E drawer entry ${stamp}`;
      const note = `alpha-${stamp}`;
      // ⚠ У довіднику з `CodeMode = Auto` код нового запису видає сервер, і код у запиті має бути порожнім.
      const auto = definition.codeMode === 'Auto';
      const entry = await admin.request.post(`/api/v1/registries/${registry}/entries`, {
        data: {
          id: null,
          registryDefId: definition.id,
          code: auto ? '' : 'EDR1',
          display: { values: { en: display } },
          parentEntryId: null,
          values: { [FieldCode]: note },
        },
      });
      expect(entry.status(), `створення запису: ${await entry.text()}`).toBe(201);
      const entryId = ((await entry.json()) as { id: number }).id;

      const draft: Seed = { registry, registryDefId: definition.id, entryId, entryCode: '', display, note };
      const row = await readRow(admin.request, draft);
      expect(noteOf(row), 'значення поля після створення').toBe(note);
      seed = { ...draft, entryCode: row.code };
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }) => {
    if (seed === null) return;
    const admin = await signedIn(browser);
    try {
      const removed = await admin.request.delete(`/api/v1/registries/${seed.registry}/entries/${String(seed.entryId)}`);
      expect([204, 404], `видалення запису: ${String(removed.status())} ${await removed.text()}`).toContain(removed.status());
    } finally {
      await admin.close();
    }
  });

  test('відкриття з сітки → ?panel=entry-<id>; Esc → фокус на відкривачі; перезавантаження відкриває той самий запис', async ({ page }) => {
    const data = seed;
    if (data === null) throw new Error('beforeAll не завів дані');
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    await page.goto(`/admin/registries/${data.registry}/entries`);
    const grid = page.getByRole('grid');
    await expect(grid).toBeVisible({ timeout: 30_000 });
    await expect(page).not.toHaveURL(/[?&]panel=/);

    const opener = grid.getByRole('button', { name: `Open entry: ${data.entryCode}`, exact: true });
    const drawer = drawerOf(page, data);
    await expect(drawer).toHaveCount(0);

    // --- 1. Відкриття з сітки: адреса й дані саме цього запису (лінивий чанк доїхав і змонтувався).
    await opener.click();
    await expect(page).toHaveURL(panelUrl(data));
    await expect(drawer).toBeVisible({ timeout: 15_000 });
    await expect(drawer).toContainText(data.display);
    await expect(pairValue(drawer, 'Code')).toHaveText(data.entryCode);
    await expect(pairValue(drawer, FieldName)).toHaveText(data.note);

    // --- 2. Esc: `panel` зникає з адреси, фокус — на відкривачі в рядку.
    await page.keyboard.press('Escape');
    await expect(page).not.toHaveURL(/[?&]panel=/);
    await expect(drawer).toBeHidden({ timeout: 10_000 });
    await expect(opener, 'фокус повернувся на відкривач запису').toBeFocused();

    // --- 3. Глибоке посилання: та сама адреса після перезавантаження відкриває шторку одразу.
    await opener.click();
    await expect(page).toHaveURL(panelUrl(data));
    await expect(drawer).toBeVisible({ timeout: 15_000 });
    await page.reload();
    await expect(page).toHaveURL(panelUrl(data));
    await expect(drawer).toBeVisible({ timeout: 30_000 });
    await expect(drawer).toContainText(data.display);
    await expect(pairValue(drawer, FieldName)).toHaveText(data.note);

    // Кнопка закриття (а не лише Esc) теж знімає `panel` з адреси.
    await drawer.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page).not.toHaveURL(/[?&]panel=/);
    await expect(drawer).toBeHidden({ timeout: 10_000 });
    await expect(grid).toBeVisible();
  });

  test('правка поля в шторці: нове значення в шторці й після перезавантаження, потім повертається', async ({ page }) => {
    const data = seed;
    if (data === null) throw new Error('beforeAll не завів дані');
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const me = await page.request.get('/api/v1/me');
    expect(me.ok(), `GET /me: ${String(me.status())}`).toBe(true);
    const permissions = ((await me.json()) as { permissions: string[] }).permissions;
    test.skip(
      !permissions.includes('Registry.EditData'),
      'у e2e-admin на стенді немає Registry.EditData — шторка показує запис лише для читання, правити нічим.',
    );

    const changed = `beta-${String(Date.now())}`;
    try {
      // Глибоке посилання одразу на шторку запису.
      await page.goto(`/admin/registries/${data.registry}/entries?panel=entry-${String(data.entryId)}`);
      const drawer = drawerOf(page, data);
      await expect(drawer).toBeVisible({ timeout: 30_000 });
      await expect(pairValue(drawer, FieldName)).toHaveText(data.note);

      await drawer.getByRole('button', { name: 'Edit', exact: true }).click();
      const editor = page.getByRole('dialog', { name: 'Edit', exact: true });
      await expect(editor).toBeVisible({ timeout: 15_000 });
      const input = editor.getByRole('textbox', { name: new RegExp(`^${FieldName}`) });
      // Форма засівається з `GET …/entries/{id}`: поки значення не приїхало, правити зарано.
      await expect(input).toHaveValue(data.note, { timeout: 15_000 });
      await input.fill(changed);
      await editor.getByRole('button', { name: 'Save', exact: true }).click();
      await expect(editor).toBeHidden({ timeout: 15_000 });

      // Шторка лишилася відкритою й перечитала запис.
      await expect(page).toHaveURL(panelUrl(data));
      await expect(pairValue(drawer, FieldName)).toHaveText(changed, { timeout: 15_000 });

      // Збережено на сервері: після перезавантаження та сама шторка показує нове значення.
      await page.reload();
      await expect(drawer).toBeVisible({ timeout: 30_000 });
      await expect(pairValue(drawer, FieldName)).toHaveText(changed);
      expect(noteOf(await readRow(page.request, data)), 'сервер віддає нове значення').toBe(changed);
    } finally {
      // Повернути значення — базова лінія запису для решти прогонів.
      await writeNote(page.request, data, data.note);
    }
    expect(noteOf(await readRow(page.request, data)), 'значення повернуто').toBe(data.note);
  });
});
