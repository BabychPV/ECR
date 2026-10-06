import { expect, test, type Page } from '@playwright/test';

/**
 * Дерево таблиць документа і режим «одна таблиця» (`UI-22`) — на живому стенді.
 *
 * Приймання картки (`UI-ADOPTION-TASKS-2026-10-06.md`, UI-22):
 *   1) дерево з усіма таблицями аркуша, фільтр, стан і лічильник помилок;
 *   2) клік або Enter переносить фокус на таблицю й оновлює `?table=`; посилання відновлює вибір;
 *   3) перехід між таблицями не губить незбережені правки (сітка не розмонтовується);
 *   4) перша сітка не повільніша, ніж була в стосі (замір у журналі прогону);
 *   6) на вузькому екрані дерево — шар поверх, після вибору ховається.
 * П.5 (axe обох тем) — гейт `a11y (dark)`/`a11y (light)`.
 *
 * ⚠ Готує хмарна лінія, ганяє «Аудит» (`tools/e2e-stand.ps1`): у хмарі стенда немає.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/** Стенд: 90 таблиць + гейтова (`DistributionProfile`). */
const ExpectedTables = 91;

test.describe('Дерево таблиць документа (UI-22)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('дерево, одна таблиця на екрані, вибір в адресі, фокус на таблицю', async ({ page }) => {
    test.slow();
    await signIn(page);

    const startedAt = Date.now();
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    const tree = page.getByRole('tree');
    await expect(tree, 'дерева таблиць немає').toBeVisible({ timeout: 60_000 });
    await expect(tree.getByRole('treeitem')).toHaveCount(ExpectedTables, { timeout: 30_000 });

    const grid = page.locator('revo-grid:visible');
    await expect(grid.locator('revogr-data .rgCell').first(), 'рядки першої таблиці не з’явилися').toBeVisible({
      timeout: 60_000,
    });
    console.log(`[ui22] перша сітка з даними за ${String(Date.now() - startedAt)} мс`);

    // На екрані рівно одна таблиця.
    await expect(page.locator('[data-table-selected="true"]')).toHaveCount(1);
    await expect(page.locator('revo-grid:visible')).toHaveCount(1);

    // ── Дерево — одна зупинка Tab; стрілки й Enter.
    await expect(tree.locator('[role="treeitem"][tabindex="0"]')).toHaveCount(1);
    await tree.locator('[role="treeitem"][tabindex="0"]').focus();
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    const target = page.locator(':focus');
    const targetId = await target.getAttribute('data-tree-table');
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(/[?&]table=/);
    const selectedSlot = page.locator(`[data-table-slot="${targetId ?? ''}"]`);
    await expect(selectedSlot).toHaveAttribute('data-table-selected', 'true');
    // Фокус — на заголовку вибраної таблиці, не на `body`.
    await expect
      .poll(() => page.evaluate(() => document.activeElement?.hasAttribute('data-table-title') ?? false))
      .toBe(true);

    // ── Посилання відновлює вибір.
    const link = page.url();
    await page.goto(link);
    await expect(page.locator(`[data-table-slot="${targetId ?? ''}"]`)).toHaveAttribute('data-table-selected', 'true', {
      timeout: 60_000,
    });

    // ── Фільтр.
    await page.getByRole('searchbox', { name: /Filter tables/i }).fill('zzz-no-such-table');
    await expect(page.getByRole('tree')).toHaveCount(0);
    await page.getByRole('button', { name: /Clear filter/i }).click();
    await expect(page.getByRole('tree').getByRole('treeitem')).toHaveCount(ExpectedTables);
  });

  test('перехід між таблицями не розмонтовує сітку з правкою', async ({ page }) => {
    test.slow();
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    const first = page.locator('[data-table-selected="true"]');
    await expect(first.locator('revogr-data .rgCell').first()).toBeVisible({ timeout: 60_000 });
    const firstId = await first.getAttribute('data-table-slot');

    const items = page.getByRole('tree').getByRole('treeitem');
    await items.nth(1).click();
    await expect(page.locator(`[data-table-slot="${firstId ?? ''}"]`)).toBeHidden();
    // ⛔ Прихована, а не знята: сітка лишається в DOM — разом із правками й Undo.
    await expect(page.locator(`[data-table-slot="${firstId ?? ''}"]`)).toHaveAttribute('data-table-mounted', 'true');

    await items.nth(0).click();
    await expect(page.locator(`[data-table-slot="${firstId ?? ''}"]`)).toBeVisible();
  });

  test('вузький екран: дерево — шар поверх, після вибору ховається', async ({ page }) => {
    test.slow();
    await page.setViewportSize({ width: 800, height: 900 });
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    const toggle = page.getByTestId('table-navigator-toggle');
    await expect(toggle).toBeVisible({ timeout: 60_000 });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(page.getByRole('tree')).toBeVisible();
    await page.getByRole('tree').getByRole('treeitem').nth(2).click();
    await expect(page.getByTestId('table-navigator')).toBeHidden();
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  // ⚠ Роль, а не підпис: підпис «Password» має й кнопка-тумблер видимості (`LoginPage.tsx`).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
