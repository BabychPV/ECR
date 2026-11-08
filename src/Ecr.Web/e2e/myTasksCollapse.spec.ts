import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ UAT my-tasks-collapse (людина: «також в май таск додай колапс»):
 * 1) блок шухляди «My tasks» згортається, і після перезавантаження лишається згорнутим;
 * 2) місяці одного аркуша одного документа в «Awaiting my approval» - один рядок-група, що розгортається до місяців;
 * 3) (P3, Admin → Jobs) прапорець «Only my jobs» стоїть на одній лінії з полями фільтрів, на 375 px без
 *    горизонтальної прокрутки.
 * ⚠ Другий місяць подається, лише якщо стенд дає його подати (документ може не мати сусіднього місяця); тоді
 * перевірка групи пропускається з анотацією, а не мовчки.
 */

interface DocumentTable {
  readonly sheetDefId: number;
  readonly sheetCode: string;
}

interface ApprovalQueue {
  readonly items: readonly { readonly documentId: number; readonly sheetDefId: number; readonly periodKey: number }[];
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function openMyTasks(page: Page): Promise<void> {
  await page.getByRole('button', { name: /^(My tasks|Мои задачи|Менің тапсырмаларым)/ }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
}

test.describe('myTasksCollapse: My tasks sections collapse and approval months group into one row', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('a collapsed section stays collapsed after reload and a grouped row expands to its months', async ({ page }) => {
    await signIn(page);
    const { documentId, periodKey } = await createMultiSheetDocument(page, 1, 1);
    const id = String(documentId);
    const tables = await ok<DocumentTable[]>(
      await page.request.get(`/api/v1/documents/${id}/tables?periodKey=${String(periodKey)}`),
      'GET tables',
    );
    const sheet = tables[0] as DocumentTable;
    await ok(
      await page.request.post(`/api/v1/documents/${id}/submit`, {
        data: { sheetDefId: sheet.sheetDefId, periodKey, acknowledgeWarnings: true },
      }),
      'submit month 1',
    );
    const month = periodKey % 100;
    const secondPeriod = month < 12 ? periodKey + 1 : periodKey - 1;
    const second = await page.request.post(`/api/v1/documents/${id}/submit`, {
      data: { sheetDefId: sheet.sheetDefId, periodKey: secondPeriod, acknowledgeWarnings: true },
    });

    const queue = await ok<ApprovalQueue>(await page.request.get('/api/v1/me/approval-queue?limit=50'), 'GET approval queue');
    const mine = queue.items.filter((item) => item.documentId === documentId && item.sheetDefId === sheet.sheetDefId);
    expect(mine.length, 'поданий аркуш не потрапив у чергу погоджень').toBeGreaterThanOrEqual(1);

    // ── Згортання переживає перезавантаження ──────────────────────────────────────────────────
    await page.goto('/');
    await openMyTasks(page);
    const toggle = page.locator('[data-section-toggle="my-tasks-approval"]');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await toggle.focus();
    await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();
    await expect(page.locator('#my-tasks-approval-body')).toBeHidden();

    await page.reload();
    await openMyTasks(page);
    await expect(page.locator('[data-section-toggle="my-tasks-approval"]')).toHaveAttribute('aria-expanded', 'false');
    await page.locator('[data-section-toggle="my-tasks-approval"]').click();
    await expect(page.locator('#my-tasks-approval-body')).toBeVisible();

    // ── Група місяців ─────────────────────────────────────────────────────────────────────────
    if (!second.ok() || mine.length < 2) {
      test.info().annotations.push({
        type: 'grouping-not-checked',
        description: `стенд не дав подати другий місяць (${String(second.status())}) — групу не перевірено`,
      });

      return;
    }
    const group = page.locator(`[data-approval-group="${id}:${String(sheet.sheetDefId)}"]`);
    const groupToggle = group.locator('[data-action="toggle-approval-group"]');
    await expect(groupToggle).toHaveAttribute('aria-expanded', 'false');
    await groupToggle.click();
    await expect(groupToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(group.locator(`[data-approval-document="${id}"]`)).toHaveCount(mine.length);
    await expect(group.locator(`[data-approval-document="${id}"] a`).first()).toHaveAttribute('href', /periodKey=\d{6}&sheet=/);
  });

  test('jobs filter: the Only my jobs checkbox lines up with the search field and fits a phone screen', async ({ page }) => {
    await signIn(page);
    await page.setViewportSize({ width: 1335, height: 900 });
    await page.goto('/admin/jobs');
    const search = page.locator('[data-filter-bar] input').first();
    const checkbox = page.locator('[data-jobs-mine-only]');
    await expect(search).toBeVisible();
    await expect(checkbox).toBeVisible();
    const s = await search.boundingBox();
    const c = await checkbox.boundingBox();
    expect(s).not.toBeNull();
    expect(c).not.toBeNull();
    const centre = (box: { y: number; height: number }): number => box.y + box.height / 2;
    expect(Math.abs(centre(c as { y: number; height: number }) - centre(s as { y: number; height: number }))).toBeLessThanOrEqual(4);

    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto('/admin/jobs');
    await expect(checkbox).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
  });

  test('project settings: every action shows a one-line description and the automatic period says why', async ({ page }) => {
    await signIn(page);
    const { documentId, periodKey } = await createMultiSheetDocument(page, 1, 1);
    const card = await ok<{ readonly projectId: number }>(
      await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(periodKey)}`),
      'GET document',
    );
    // ✎ Projects (08.11): налаштування — вкладка Settings проєкту; клон — на вкладці Lifecycle.
    await page.goto(`/projects/${String(card.projectId)}/settings`);

    // ✎ UAT project-settings (людина 08.11): повна назва дії + опис поруч, пов'язаний через aria-describedby.
    for (const name of [/^Pin current period…$/, /^Edit approval route…$/, /^Manage period policies…$/]) {
      const button = page.getByRole('button', { name });
      await expect(button).toBeVisible();
      const describedBy = (await button.getAttribute('aria-describedby')) ?? '';
      expect(describedBy, String(name)).not.toBe('');
      await expect(page.locator(`[id="${describedBy}"]`)).toBeVisible();
      expect(((await page.locator(`[id="${describedBy}"]`).textContent()) ?? '').length).toBeGreaterThan(20);
    }

    // Автоматичний період — «найраніший відкритий місяць», а не «за датою».
    await expect(page.locator('[data-current-period-explain]')).toHaveText(/earliest open month|no month is open/);
  });
});
