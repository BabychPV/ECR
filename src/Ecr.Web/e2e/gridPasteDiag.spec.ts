import { expect, test, type Page } from '@playwright/test';

// ТИМЧАСОВИЙ діагностичний спек (не для злиття): де фокус і що поруч, коли вставка після Esc мовчить.
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('PasteDiag', () => {
  test.skip(PeriodKey === '' || DocumentId === '', 'стенда немає');

  test('diag escape', async ({ page }: { page: Page }) => {
    await page.addInitScript(() => {
      const w = window as unknown as { __log: string[]; __d: (e: Element | null) => string };
      w.__log = [];
      const t0 = performance.now();
      const d = (e: Element | null): string =>
        e === null
          ? 'null'
          : `${e.tagName.toLowerCase()}${e.id ? '#' + e.id : ''}${typeof e.className === 'string' && e.className ? '.' + e.className.split(' ').slice(0, 2).join('.') : ''}${e.getAttribute('role') ? `[role=${e.getAttribute('role')}]` : ''}${e.hasAttribute('data-table-title') ? '[table-title]' : ''}`;
      w.__d = d;
      const add = (s: string): void => {
        w.__log.push(`${Math.round(performance.now() - t0)}ms ${s}`);
      };
      document.addEventListener('focusin', (e) => add(`focusin ${d(e.target as Element)}`), true);
      document.addEventListener('focusout', (e) => add(`focusout ${d(e.target as Element)}`), true);
      document.addEventListener('keydown', (e) => add(`keydown ${e.key}`), true);
      document.addEventListener('pointerdown', (e) => add(`pointerdown ${d(e.target as Element)}`), true);
    });

    await page.goto('/login');
    await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
    await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
    await page.keyboard.press('Enter');
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    await expect(page.locator('revo-grid').first().locator('revogr-data .rgCell').first()).toBeVisible({ timeout: 30_000 });
    await page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell').nth(0).click();
    await page.keyboard.press('Enter');
    await page.keyboard.type('5');
    await page.keyboard.press('Escape');

    const info = await page.evaluate(() => {
      const w = window as unknown as { __log: string[]; __d: (e: Element | null) => string };
      const sel = window.getSelection();
      const anchor = sel?.anchorNode ?? null;
      const target = anchor === null ? document.activeElement : anchor instanceof Element ? anchor : anchor.parentElement;
      const modals = [...document.querySelectorAll('[role="dialog"], [aria-modal="true"], dialog[open]')].map((e) => w.__d(e));
      const data = new DataTransfer();
      data.setData('text/plain', '77');
      const ev = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
      (target ?? document.body).dispatchEvent(ev);
      return {
        active: w.__d(document.activeElement),
        target: w.__d(target),
        modals,
        prevented: ev.defaultPrevented,
        log: w.__log.slice(-25),
      };
    });

    const patch = await page
      .waitForResponse((r) => r.request().method() === 'PATCH' && r.url().includes(`/documents/${DocumentId}`), { timeout: 5_000 })
      .then(() => true)
      .catch(() => false);
    expect(patch, JSON.stringify(info)).toBe(true);
  });
});
