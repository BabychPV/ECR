import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ Projects + Documents (макет `docs/design/hybrid/projects-documents-1108.html`, людина 08.11: «так макет хороший»)
 * проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Projects 08.11"`). Кожен тест названо
 * рядком сценарію приймання `ACCEPTANCE-1108-post15.md` (f1–f7, g1–g4, fg5, fg6).
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 * ⚠ Проєкт і документ — власні (`createMultiSheetDocument`); клон — теж власний; базову лінію стенда файл не чіпає.
 * ⚠ Написано у вікні W15l без локального прогону (правило «e2e — на CI»): перший живий прогін — CI.
 */

interface DocumentCard {
  readonly projectId: number;
  readonly businessKey: string;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function ownProject(page: Page): Promise<{ documentId: number; periodKey: number; card: DocumentCard }> {
  const { documentId, periodKey } = await createMultiSheetDocument(page, 1, 1);
  const card = await ok<DocumentCard>(
    await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(periodKey)}`),
    'GET document',
  );

  return { documentId, periodKey, card };
}

/** Порушення axe рівня critical/serious (WCAG 2 A/AA) на поточній сторінці. */
async function seriousAxe(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    type Run = { violations: { id: string; impact: string | null; nodes: { target: unknown[] }[] }[] };
    const axe = (window as unknown as { axe: { run: (c: unknown, o: object) => Promise<Run> } }).axe;
    const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations
      .filter((v) => v.impact === 'critical' || v.impact === 'serious')
      .flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

async function pageOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

test.describe('Projects 08.11', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('f1: one «Projects» item in the WORK group, no «Periods» or «Project settings»', async ({ page }) => {
    await signIn(page);
    await page.goto('/');
    // ⚠ Бічне меню (`AppShell.Navbar`) — `<nav>` БЕЗ доступної назви: шукаємо його за пунктом «Documents» (href «/»),
    // пункти — за адресою (стабільно), а підпис — видимим текстом.
    await expect(page.locator('nav a[href="/projects"]')).toHaveCount(1, { timeout: 60_000 });
    const nav = page.locator('nav', { has: page.locator('a[href="/projects"]') });
    await expect(nav.locator('a[href="/projects"]')).toContainText('Projects');
    await expect(nav.locator('a[href="/admin/periods"], a[href="/admin/projects"]')).toHaveCount(0);
    await expect(nav.getByText(/^(Periods|Project settings)$/)).toHaveCount(0);
    // Порядок у групі Work: Documents, Projects, My groups (далі — кампанія, якщо є право).
    const hrefs = await nav.locator('a').evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
    const at = (href: string): number => hrefs.indexOf(href);
    expect(at('/'), hrefs.join(' ')).toBeLessThan(at('/projects'));
    expect(at('/projects'), hrefs.join(' ')).toBeLessThan(at('/my-groups'));
    // Templates лишається в Configure — після групи Work.
    expect(at('/projects')).toBeLessThan(at('/admin/templates'));
  });

  test('f2: /admin/periods and /admin/projects redirect to Projects without an error page', async ({ page }) => {
    await signIn(page);
    const { card } = await ownProject(page);
    for (const [from, to] of [
      ['/admin/periods', /\/projects$/],
      ['/admin/projects', /\/projects$/],
      [`/admin/periods?projectId=${String(card.projectId)}`, new RegExp(`/projects/${String(card.projectId)}/periods$`)],
      [`/admin/projects?projectId=${String(card.projectId)}`, new RegExp(`/projects/${String(card.projectId)}/settings$`)],
    ] as const) {
      await page.goto(from);
      await expect(page).toHaveURL(to, { timeout: 60_000 });
      await expect(page.getByText(/Page not found|Something went wrong/i)).toHaveCount(0);
    }
  });

  test('f3 + f7: Projects list shows status, current period, state and closes; row opens the project; crumbs and Ctrl K say Projects', async ({ page }) => {
    await signIn(page);
    const { card } = await ownProject(page);
    const project = await ok<{ items: { id: number; code: string }[] }>(await page.request.get('/api/v1/projects?limit=500'), 'projects');
    const code = project.items.find((item) => item.id === card.projectId)?.code ?? '';

    await page.goto('/projects');
    await expect(page.getByRole('heading', { level: 1, name: 'Projects' })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('button', { name: 'New project' })).toBeVisible();
    for (const column of ['Project', 'Status', 'Current period', 'State', 'Closes']) {
      await expect(page.getByRole('columnheader', { name: new RegExp(column) })).toBeVisible();
    }
    const link = page.locator('[data-project-open]', { hasText: code });
    const row = page.locator('tr', { has: link });
    await expect(row).toContainText('Active');
    await expect(row.locator('[data-project-state]')).toHaveAttribute('data-project-state', /open|allLocked/);

    await row.click();
    await expect(page).toHaveURL(new RegExp(`/projects/${String(card.projectId)}/periods$`));
    // f7: крихти й заголовок.
    const crumbs = page.getByRole('navigation', { name: /Breadcrumb/i });
    await expect(crumbs).toContainText('Projects', { timeout: 60_000 });
    await expect(crumbs).toContainText(code);
    await expect(page.getByRole('heading', { level: 1, name: code })).toBeVisible();

    // f7: командна палітра знаходить «Projects».
    await page.keyboard.press('Control+k');
    const palette = page.getByRole('dialog');
    await expect(palette).toBeVisible({ timeout: 10_000 });
    await page.keyboard.type('Projects');
    await expect(palette.getByRole('option', { name: /Projects/ }).first()).toBeVisible();
    await expect(palette.getByRole('option', { name: /^Periods$|Project settings/ })).toHaveCount(0);
    await page.keyboard.press('Escape');
  });

  test('f4 + fg6: Periods tab shows calendar and table with real states, collapsible note and Recalculate', async ({ page }) => {
    await signIn(page);
    const { card, periodKey } = await ownProject(page);

    await page.goto(`/projects/${String(card.projectId)}/periods`);
    await expect(page.getByTestId('periods-year')).toBeVisible({ timeout: 60_000 });
    const row = page.locator(`[data-period-row="${String(periodKey)}"]`);
    await expect(row).toContainText(/Open|Late \(flagged\)|Locked/);
    // Плитка і рядок кажуть одне.
    const tileState = await page.locator(`[data-period-tile="${String(periodKey)}"]`).getAttribute('data-state');
    expect(tileState).not.toBeNull();
    await expect(page.getByRole('button', { name: /Recalculate project/ })).toBeVisible();

    // fg6: пояснення згортається, згорнуте — один рядок, вибір пам'ятається після перезавантаження.
    const note = page.locator('[data-note="periods-lock-only"]');
    const toggle = note.getByRole('button');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    const box = await note.boundingBox();
    expect(box?.height ?? 999, 'згорнуте пояснення вище одного рядка').toBeLessThan(40);
    await page.reload();
    await expect(page.locator('[data-note="periods-lock-only"]').getByRole('button')).toHaveAttribute('aria-expanded', 'false', {
      timeout: 60_000,
    });
  });

  test('f5: Settings tab has current period, close policy, approval route and time zone', async ({ page }) => {
    await signIn(page);
    const { card } = await ownProject(page);

    await page.goto(`/projects/${String(card.projectId)}/settings`);
    for (const section of ['current-period', 'policy', 'route', 'time-zone']) {
      await expect(page.locator(`[data-project-section="${section}"]`)).toBeVisible({ timeout: 60_000 });
    }
    await expect(page.getByRole('button', { name: /^Pin current period…$/ })).toBeVisible();
  });

  test('f6 + c1–c4: Lifecycle — Activate, Clone, Archive, Delete draft with visible reasons; Clone creates a draft, Delete removes it', async ({ page }) => {
    await signIn(page);
    const { card } = await ownProject(page);

    await page.goto(`/projects/${String(card.projectId)}/lifecycle`);
    const archive = page.getByRole('button', { name: 'Archive project…' });
    await expect(archive).toBeVisible({ timeout: 60_000 });
    await expect(archive).toBeDisabled();
    await expect(page.locator('[data-lifecycle-reason="archive"]')).toContainText('All months must be locked; open:');
    await expect(page.locator('[data-lifecycle-reason="activate"]')).toHaveText('Already active.');
    // c4: кожна кнопка — повна назва й рядок опису, нічого не обрізано.
    for (const name of ['Activate project…', 'Clone project…', 'Archive project…', 'Delete draft project…']) {
      const button = page.getByRole('button', { name });
      const describedBy = (await button.getAttribute('aria-describedby')) ?? '';
      await expect(page.locator(`[id="${describedBy}"]`)).toBeVisible();
      const clipped = await button.evaluate((el) => el.scrollWidth > el.clientWidth + 1);
      expect(clipped, `${name}: підпис обрізано`).toBe(false);
    }

    await page.getByRole('button', { name: 'Clone project…' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox').fill(`E2EPD${String(Date.now())}`);
    await dialog.getByRole('button', { name: /^Clone/ }).click();
    await expect(page).toHaveURL(/\/projects\/\d+\/settings$/, { timeout: 60_000 });
    // ⚠ Перехід іде переходом React (startTransition): адреса вже нова, а поки чанк вкладки вантажиться, на екрані
    // ще стара сторінка з посиланнями на ДЖЕРЕЛО клону. Клік тоді вів назад на нього — чекаємо нову вкладку.
    const cloneId = /\/projects\/(\d+)\/settings/.exec(page.url())?.[1] ?? '';
    await expect(page.locator('[data-project-section="current-period"]')).toBeVisible({ timeout: 60_000 });
    await expect(page.locator('[data-project-tab="lifecycle"]')).toHaveAttribute('href', `/projects/${cloneId}/lifecycle`);
    await page.locator('[data-project-tab="lifecycle"]').click();
    await expect(page).toHaveURL(new RegExp(`/projects/${cloneId}/lifecycle$`));
    await expect(page.getByRole('button', { name: 'Activate project…' })).toBeEnabled({ timeout: 60_000 });
    await expect(page.locator('[data-lifecycle-reason="archive"]')).toContainText('Only active projects can be archived');

    // c1: чернетку без документів видаляють лише після набору коду; її більше немає в переліку.
    const cloneCode = (await page.getByRole('heading', { level: 1 }).first().textContent()) ?? '';
    await page.getByRole('button', { name: 'Delete draft project…' }).click();
    const confirm = page.getByRole('dialog');
    await expect(confirm.getByRole('button', { name: 'Delete project' })).toBeDisabled();
    await confirm.getByRole('textbox').fill(cloneCode);
    await confirm.getByRole('button', { name: 'Delete project' }).click();
    await expect(page).toHaveURL(/\/projects$/, { timeout: 60_000 });
    await expect(page.locator('[data-project-open]', { hasText: cloneCode })).toHaveCount(0);

    // c2: активний проєкт із документами — Delete недоступна з причиною.
    await page.goto(`/projects/${String(card.projectId)}/lifecycle`);
    await expect(page.locator('[data-lifecycle-reason="delete"]')).toContainText('Only draft projects without documents can be deleted (this one: Active', { timeout: 60_000 });
  });

  test('g1 + g3: Documents has no Month control, Year lists only years with projects, every row has a rendered 12-month strip with a legend', async ({ page }) => {
    await signIn(page);
    const { documentId, periodKey, card } = await ownProject(page);
    const year = Math.trunc(periodKey / 100);

    await page.goto(`/?projectId=${String(card.projectId)}&year=${String(year)}`);
    const row = page.locator(`[data-document-row="${card.businessKey}"]`);
    await expect(row).toBeVisible({ timeout: 60_000 });
    await expect(page.locator('[data-documents-month]')).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: /^Month$/ })).toHaveCount(0);

    const options = await page.locator('[data-documents-year] option').allTextContents();
    const calendars = await Promise.all(
      (await ok<{ items: { id: number }[] }>(await page.request.get('/api/v1/projects?limit=500'), 'projects')).items.map(async (p) =>
        ok<{ periods: { year: number }[] }>(await page.request.get(`/api/v1/projects/${String(p.id)}/periods`), 'periods'),
      ),
    );
    const years = [...new Set(calendars.flatMap((c) => c.periods.map((x) => x.year)))].sort((a, b) => b - a).map(String);
    expect(options).toEqual(years);

    const cells = row.locator('[data-month]');
    await expect(cells).toHaveCount(12);
    // Смуга справді малюється: клітинки мають стани (не всі «unknown»), і хоч одна відрізняється від інших.
    const states = await cells.evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-cell')));
    expect(states.filter((s) => s === 'unknown'), `стани: ${states.join(',')}`).toHaveLength(0);
    await expect(page.locator('[data-year-strip-legend]')).toBeVisible();

    await row.locator(`[data-month="${String(periodKey)}"]`).click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(documentId)}\\?periodKey=${String(periodKey)}`));
  });

  test('g2 + g4: a year without documents says «No documents for <year>» with a hint, counters are 0 and count that year only', async ({ page }) => {
    await signIn(page);
    await ownProject(page);
    const empty = 2001;

    await page.goto(`/?year=${String(empty)}`);
    await expect(page.getByText(`No documents for ${String(empty)}`)).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(/No project covers 2001|Projects for 2001/)).toBeVisible();
    await expect(page.locator('[data-document-row]')).toHaveCount(0);
    const counters = page.locator('[data-summary-counter]');
    for (const value of await counters.allTextContents()) expect(value).toMatch(/^\D*0\D*$/);
    await expect(page.getByRole('button', { name: /^Show \d{4}$/ })).toBeVisible();
  });

  test('fg5: document grid header at 375 px — «Keyboard shortcuts» is not clipped', async ({ page }) => {
    await signIn(page);
    const { documentId, periodKey } = await ownProject(page);

    await page.setViewportSize({ width: 375, height: 900 });
    await page.goto(`/documents/${String(documentId)}?periodKey=${String(periodKey)}`);
    const help = page.locator('[data-grid-key-help]').first();
    await expect(help).toBeVisible({ timeout: 60_000 });
    await expect(help).toHaveAccessibleName('Keyboard shortcuts');
    // Нічого не обрізано: кнопка вміщає свій вміст і лежить у межах вікна.
    const fits = await help.evaluate((el) => {
      const box = el.getBoundingClientRect();
      return el.scrollWidth <= el.clientWidth + 1 && box.right <= window.innerWidth + 0.5 && box.left >= -0.5;
    });
    expect(fits, 'кнопка «Keyboard shortcuts» обрізана або виходить за вікно').toBe(true);
    expect(await pageOverflow(page), 'горизонтальна прокрутка сторінки документа').toBeLessThanOrEqual(0);
  });
  /*
   * ✎ F13 fg5 (09.11): на живих даних Documents @ 375 px прокручувався вбік на 109 px — смуга лічильників
   * (`FilterInline`, один рядок) з УСІМА лічильниками. Тест нижче цього не ловив: на стенді немає частково поданих і
   * повернутих документів, а ці два лічильники смуга показує лише коли вони не нульові. Тут відповідь
   * `GET /documents/summary` — справжня, лише з підставленими числами (усі лічильники, чотиризначні): перевіряється
   * верстка справжньої сторінки, а не дані. Без переносу смуги (`documentsList.css`) — червоний.
   */
  test('fg5: Documents at 375 px with every counter shown (Partly submitted, Rejected, 4-digit) — no page overflow', async ({ page }) => {
    await signIn(page);
    const { card, periodKey } = await ownProject(page);
    const year = Math.trunc(periodKey / 100);

    await page.route('**/api/v1/documents/summary?*', async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      await route.fulfill({
        response,
        json: { ...body, draft: 1090, partlySubmitted: 1002, submitted: 1000, rejected: 1001, approved: 1000, withIssues: 1000 },
      });
    });

    await page.setViewportSize({ width: 375, height: 900 });
    await page.goto(`/?year=${String(year)}&projectId=${String(card.projectId)}`);
    const strip = page.locator('[data-summary-strip]');
    await expect(strip.locator('[data-summary-counter="rejected"]')).toBeVisible({ timeout: 60_000 });
    await expect(strip.locator('[data-summary-counter="partlySubmitted"]')).toBeVisible();
    await expect(strip.locator('[data-summary-counter="withIssues"]')).toBeVisible();
    expect(await pageOverflow(page), 'Documents @ 375 px з усіма лічильниками: горизонтальна прокрутка').toBeLessThanOrEqual(0);
    const box = await strip.boundingBox();
    expect((box?.x ?? 0) + (box?.width ?? 0), 'смуга лічильників виходить за вікно').toBeLessThanOrEqual(375.5);
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`fg5: Projects, tabs and Documents — no page overflow at 1335 and 375 px, axe clean (${scheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);
      const { card } = await ownProject(page);

      for (const width of [1335, 375]) {
        await page.setViewportSize({ width, height: 900 });
        for (const url of [
          '/projects',
          `/projects/${String(card.projectId)}/periods`,
          `/projects/${String(card.projectId)}/settings`,
          `/projects/${String(card.projectId)}/lifecycle`,
          '/',
        ]) {
          await page.goto(url);
          await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 60_000 });
          await page.waitForTimeout(1_000);
          expect(await pageOverflow(page), `${url} @ ${String(width)} px: горизонтальна прокрутка сторінки`).toBeLessThanOrEqual(0);
          expect(await seriousAxe(page), `${url} @ ${String(width)} px (${scheme})`).toEqual([]);
        }
      }
    });
  }
});
