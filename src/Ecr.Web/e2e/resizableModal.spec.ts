import { expect, test, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';

/**
 * Вікно формули методології: на весь екран і ручка розміру (P44) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: у jsdom немає розкладки, тож ні те, що кут іде за курсором,
 * ні те, що Monaco справді росте з вікном (`automaticLayout`, ланцюжок `flex` у
 * `ResizableModal.css`), там не видно. Тут:
 *   1. «Add formula» → діалог «Formula» з редактором Monaco;
 *   2. кнопка «Full screen» → `aria-pressed=true`, вікно на всю ширину, редактор вищий, ніж був;
 *      повторний натиск — звичайний розмір;
 *   3. перетягування ручки в правому нижньому куті → вікно ширшає, розмір запам'ятовано
 *      (`localStorage`) і відновлюється після перезавантаження.
 *
 * ⚠ Дані — свої: `beforeAll` від імені `e2e-admin` (✎ P44: йому видано `Calculation.EditFormula`,
 * `tools/e2e-stand.ps1`) заводить методологію `E2E_RM_<час>` і порожню чернетку. Видалення
 * методології API не має — залишок на одноразовому стенді.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається — як `registryEntryDrawer.spec.ts`.
 *
 * Запуск: workflow `e2e-stand` з `grep="Вікно формули методології"`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

interface Seed {
  readonly methodologyId: number;
}

async function login(context: BrowserContext, userName: string, password: string): Promise<void> {
  const response = await context.request.post('/api/v1/login/local', { data: { userName, password } });
  expect(response.ok(), `вхід ${userName}: ${String(response.status())} ${await response.text()}`).toBe(true);
}

/** Методологія з порожньою чернеткою — тим самим API, що й екран. */
async function prepare(browser: Browser): Promise<Seed> {
  const stamp = String(Date.now());
  const admin = await browser.newContext();
  try {
    await login(admin, Admin.userName, Admin.password);

    const created = await admin.request.post('/api/v1/methodologies', {
      data: { code: `E2E_RM_${stamp}`, group: null, kind: 'DataDriven', nameL10n: { en: `E2E resizable ${stamp}` } },
    });
    expect(created.ok(), `методологія: ${String(created.status())} ${await created.text()}`).toBe(true);
    const methodologyId = ((await created.json()) as { id: number }).id;

    const draft = await admin.request.post(`/api/v1/methodologies/${String(methodologyId)}/versions`, {
      data: { versionNumber: '1.0', copyFromVersionId: null, level: 'Configuration' },
    });
    expect(draft.ok(), `чернетка: ${String(draft.status())} ${await draft.text()}`).toBe(true);

    return { methodologyId };
  } finally {
    await admin.close();
  }
}

/** Вхід через сторінку входу — як людина (`registryEntryDrawer.spec.ts`). */
async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.userName);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function openFormula(page: Page, seed: Seed): Promise<Locator> {
  await page.goto(`/admin/methodologies/${String(seed.methodologyId)}/versions`);
  const add = page.getByRole('button', { name: /Add formula|Добавить формулу/i });
  await expect(add, 'немає кнопки «Add formula» — чернетка не обрана або немає права').toBeVisible({
    timeout: 30_000,
  });
  await add.click();

  const dialog = page.getByRole('dialog', { name: /Formula|Формула/i });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  // ⚠ Чанк Monaco великий (~818 КБ gzip): редактор з'являється пізніше за діалог.
  await expect(dialog.locator('.monaco-editor')).toBeVisible({ timeout: 60_000 });
  return dialog;
}

async function heightOf(locator: Locator): Promise<number> {
  const box = await locator.boundingBox();
  expect(box, 'елемент не має розміру на екрані').not.toBeNull();
  return box?.height ?? 0;
}

test.describe('Вікно формули методології: на весь екран і ручка розміру (P44)', () => {
  test.describe.configure({ timeout: 180_000 });
  // Просторий екран: на весь екран редактор має куди рости, а потягнуте вікно — куди ширшати.
  test.use({ viewport: { width: 1440, height: 900 } });

  let seed: Seed | null = null;

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');
    }
    seed = await prepare(browser);
  });

  test('кнопка «Full screen» розгортає вікно, редактор росте; ручка змінює розмір і він запам’ятовується', async ({
    page,
  }) => {
    if (seed === null) throw new Error('дані не підготовлені');
    await signIn(page);
    const dialog = await openFormula(page, seed);
    const editor = dialog.locator('.monaco-editor');

    const normalEditor = await heightOf(editor);
    const toggle = dialog.getByRole('button', { name: /Full screen|Во весь экран|Толық экран/i });
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');

    // ── На весь екран ────────────────────────────────────────────────────────
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(dialog).toHaveAttribute('data-rm-size', 'full');

    const viewport = page.viewportSize();
    const fullBox = await dialog.boundingBox();
    expect(fullBox?.width ?? 0, 'на весь екран — на всю ширину').toBeGreaterThanOrEqual((viewport?.width ?? 0) - 2);

    // ⛔ Головне: зайва висота віддана полю формули (Monaco перерозклався), а не порожнечі.
    await expect.poll(() => heightOf(editor), { timeout: 10_000 }).toBeGreaterThan(normalEditor + 50);

    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await expect(dialog).toHaveAttribute('data-rm-size', 'normal');
    await expect.poll(() => heightOf(editor), { timeout: 10_000 }).toBeLessThan(normalEditor + 5);

    // ── Ручка розміру ─────────────────────────────────────────────────────────
    const before = await dialog.boundingBox();
    const handle = dialog.getByRole('button', { name: /Resize window|Изменить размер окна/i });
    const grip = await handle.boundingBox();
    expect(before, 'діалог без розміру').not.toBeNull();
    expect(grip, 'ручки не видно').not.toBeNull();
    if (before === null || grip === null) return;

    const x = grip.x + grip.width / 2;
    const y = grip.y + grip.height / 2;
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(x + 60, y + 10, { steps: 6 });
    await page.mouse.move(x + 120, y + 20, { steps: 6 });
    await page.mouse.up();

    await expect(dialog).toHaveAttribute('data-rm-size', 'custom');
    const after = await dialog.boundingBox();
    // Вікно по центру: ширшає на подвійний зсув курсора, тобто кут іде за ним.
    expect(after?.width ?? 0).toBeGreaterThan(before.width + 200);

    const width = await dialog.getAttribute('data-rm-width');
    const stored = await page.evaluate(() => globalThis.localStorage.getItem('ecr.modalSize.methodology.formula'));
    expect(stored, 'розмір не запам’ятовано').toContain('"mode":"custom"');

    // ── Після перезавантаження — той самий розмір ─────────────────────────────
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await page.reload();
    const reopened = await openFormula(page, seed);
    await expect(reopened).toHaveAttribute('data-rm-width', width ?? '');

    // Прибирання: «Згорнути» стирає запам'ятоване.
    const again = reopened.getByRole('button', { name: /Full screen|Во весь экран|Толық экран/i });
    await again.click();
    await again.click();
    expect(
      await page.evaluate(() => globalThis.localStorage.getItem('ecr.modalSize.methodology.formula')),
    ).toBeNull();
  });
});
