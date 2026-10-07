import { expect, test, type Page } from '@playwright/test';

/**
 * T4-02 (P1): Ctrl+V мовчки не вставляє після відкриття/закриття редактора
 * комірки. Chrome визначає target `paste` для нередагованого елемента за
 * ВИДІЛЕННЯМ тексту (після редактора воно лишається в `<p>` поза обгорткою
 * сітки), а не за `activeElement`; після Esc фокус узагалі на `<body>`.
 *
 * ⚠ Стенд готує `tools/e2e-stand.ps1`; без нього набір пропускається так само,
 * як `keyboardPath.spec.ts`. Живий доказ без стенда — харнес vite + Playwright
 * проти справжнього `revo-grid` (див. коміт).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Вставка після редактора комірки (T4-02)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  async function openGrid(page: Page): Promise<void> {
    await page.goto('/login');
    await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
    await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
    await page.keyboard.press('Enter');
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    await expect(page.locator('revo-grid').first().locator('revogr-data .rgCell').first()).toBeVisible({
      timeout: 30_000,
    });
  }

  /** Подія `paste` з буфером у ту саму ціль, що її вибирає браузер: елемент виділення. */
  async function pasteAtSelection(page: Page, text: string): Promise<void> {
    await page.evaluate((value) => {
      const selection = window.getSelection();
      const anchor = selection?.anchorNode;
      const target =
        anchor === null || anchor === undefined
          ? (document.activeElement ?? document.body)
          : anchor instanceof Element
            ? anchor
            : (anchor.parentElement ?? document.body);
      const data = new DataTransfer();
      data.setData('text/plain', value);
      target.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }, text);
  }

  const cellOf = (page: Page, row: number) =>
    page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell').nth(row);

  for (const scenario of ['enter', 'escape', 'click-away'] as const) {
    test(`вставка працює після редактора: ${scenario}`, async ({ page }) => {
      await openGrid(page);
      await cellOf(page, 0).click();
      // ⚠ Гонка keyCommitGate: клавіші, набрані до монтування редактора, тримає черга й відтворює
      // після; paste за ~десяток мс бачить відкритий редактор і сітка його справедливо ігнорує.
      // Тому спершу чекаємо ВІДКРИТТЯ редактора, потім завершуємо, потім чекаємо його ЗАКРИТТЯ.
      const editor = page.locator('revo-grid').first().locator('.edit-input-wrapper input');
      await page.keyboard.press('Enter');
      await expect(editor).toBeFocused();
      await page.keyboard.type('5');
      if (scenario === 'enter') await page.keyboard.press('Enter');
      if (scenario === 'escape') await page.keyboard.press('Escape');
      if (scenario === 'click-away') await cellOf(page, 1).click();
      await expect(page.locator('.edit-input-wrapper')).toHaveCount(0);

      // Нове значення щоразу: повтор того самого значення в той самий документ не дає PATCH.
      const pasted = String(100 + Math.floor(Math.random() * 800));
      const patch = page.waitForResponse(
        (response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${DocumentId}`),
        { timeout: 15_000 },
      );
      await pasteAtSelection(page, pasted);

      expect((await patch).ok(), 'вставка після редактора не дійшла до сервера').toBe(true);
    });
  }

  for (const neutral of ['body', 'heading'] as const) {
    test(`клік на нейтральну область (${neutral}) -> Ctrl+V НЕ вставляє в сітку: 0 PATCH`, async ({ page }) => {
      await openGrid(page);
      await cellOf(page, 0).click();
      await page.keyboard.press('Enter');
      await page.keyboard.press('Escape');
      await page.waitForTimeout(1_500);

      if (neutral === 'heading') await page.getByRole('heading').first().click();
      else await page.mouse.click(2, 2);

      let patches = 0;
      page.on('request', (request) => {
        if (request.method() === 'PATCH' && request.url().includes(`/documents/${DocumentId}`)) patches += 1;
      });
      await pasteAtSelection(page, '77');
      await page.waitForTimeout(2_000);

      expect(patches, 'сторонній Ctrl+V після кліку повз сітку потрапив у сітку').toBe(0);
    });
  }

  test('paste у полі вводу ПОЗА сіткою після редактора не перехоплюється сіткою', async ({ page }) => {
    await openGrid(page);
    await cellOf(page, 0).click();
    await page.keyboard.press('Enter');
    await page.keyboard.press('Escape');

    const outside = await page.evaluate(() => {
      const input = document.createElement('input');
      document.body.appendChild(input);
      input.focus();
      const data = new DataTransfer();
      data.setData('text/plain', '99');
      const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
      input.dispatchEvent(event);
      input.remove();

      return event.defaultPrevented;
    });

    expect(outside, 'сітка вкрала вставку з поля поза сіткою').toBe(false);
  });
});
