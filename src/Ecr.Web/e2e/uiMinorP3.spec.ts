import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { axeViolations } from './axeRule';
import { chooseSubmitPeriod } from './submitPeriod';

/**
 * Дрібні UI-пункти проходу C (рішення людини 27.10, lane `integ-ui-minor-p3`) - на справжньому стенді
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep uiMinorP3`):
 *
 *  1. горизонтальна прокрутка СТОРІНКИ на ширині 500 px: `/documents/<id>` і `/admin/audit`, light/dark;
 *  2. CL-18 (WCAG 2.5.8): цілі < 24 px на `/admin/periods` - гамбургер і кнопки-коди проєктів ≥ 24x24,
 *     axe `target-size` без порушень (500 і 1440 px);
 *  3. CL-22: після Esc у діалозі «Return for edits» фокус повертається на кнопку-відкривач, а не на `body` (N/5).
 *     (CL-13, фокус після «Так/Ні» в OverwriteOffer, - у `versionMigrationOverwrite.spec.ts`.)
 *
 * ⚠ Назви тестів з `uiMinorP3-assert` ЧЕРВОНІЮТЬ на коді без виправлень (доказ «падає без фіксу»); `uiMinorP3-shots` нічого
 * не стверджує, лише додає знімки (light/dark, 500 і 1440) до звіту Playwright - для листа готовності.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

type Scheme = 'light' | 'dark';
const Schemes: readonly Scheme[] = ['light', 'dark'];
const Narrow = { width: 500, height: 900 };
const Wide = { width: 1440, height: 900 };

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля (Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема - тим самим шляхом, що й `zz-walkthrough`: сховище ДО входу + медіа-запит. */
async function useScheme(page: Page, scheme: Scheme): Promise<void> {
  await page.emulateMedia({ colorScheme: scheme });
  await page.goto('/login');
  await page.evaluate((value: string) => {
    localStorage.setItem('mantine-color-scheme-value', value);
  }, scheme);
}

/**
 * Тема після входу - тим самим шляхом, що й у людини (меню користувача): вподобання з сервера (`usePreferenceSync`)
 * перекривають сховище, і «dark» без цього йшов у light (прогін 38265555757, `cellColorLive.spec.ts`). Двічі з перезавантаженням.
 */
async function applyScheme(page: Page, scheme: Scheme): Promise<void> {
  for (let pass = 0; pass < 2; pass += 1) {
    const html = page.locator('html');
    const userButton = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
    await expect(userButton).toBeVisible({ timeout: 30_000 });

    if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
      await userButton.click();
      await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
      await page.keyboard.press('Escape');
    }

    await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, {
      timeout: 15_000,
    });
    if (pass === 0) await page.reload();
  }
}
interface Overflow {
  readonly scrollWidth: number;
  readonly clientWidth: number;
  readonly offenders: readonly string[];
}

/** Горизонтальний розпір документа і найширші елементи за правий край вікна (для повідомлення про падіння). */
async function horizontalOverflow(page: Page): Promise<Overflow> {
  return page.evaluate(() => {
    const root = document.documentElement;
    const limit = root.clientWidth;
    const offenders = [...document.body.querySelectorAll('*')]
      .map((element) => ({ element, rect: element.getBoundingClientRect() }))
      .filter((item) => item.rect.width > 0 && item.rect.right > limit + 0.5)
      .sort((a, b) => b.rect.right - a.rect.right)
      .slice(0, 8)
      .map((item) => {
        const { element, rect } = item;
        const id = element.id === '' ? '' : `#${element.id}`;
        const cls = typeof element.className === 'string' ? element.className.split(' ').slice(0, 2).join('.') : '';
        const testId = element.getAttribute('data-testid') ?? '';

        return `${element.tagName.toLowerCase()}${id}.${cls}[${testId}] right=${String(Math.round(rect.right))} w=${String(Math.round(rect.width))}`;
      });

    return { scrollWidth: root.scrollWidth, clientWidth: limit, offenders };
  });
}

async function attachShot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await testInfo.attach(name, { body: await page.screenshot(), contentType: 'image/png' });
}

/** Документ відкритий і рядок дій у остаточній формі (не скелет). */
async function openDocument(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await expect(page.getByTestId('document-actions'), 'у документа немає рядка дій').toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('document-actions-pending')).toHaveCount(0, { timeout: 30_000 });
}

/** Повертає аркуш у `Draft` (законна дія `Document.Reopen`), ідемпотентно. */
async function makeSheetEditable(page: Page): Promise<void> {
  await openDocument(page);

  const activeTab = page.getByRole('tab', { selected: true });
  await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });
  if (!/Submitted|Approved/.test((await activeTab.textContent()) ?? '')) return;

  await page.getByRole('button', { name: /Return for edits|Повернути/i }).first().click();
  const dialog = page.getByRole('dialog');
  await expect(dialog, 'діалог причини не відкрився').toBeVisible({ timeout: 10_000 });
  await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: uiMinorP3, повернення базової лінії');
  await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();
  await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', { timeout: 15_000 });
}

/** Одна правка комірки вставкою з буфера - щоб у журналі `/admin/audit` був рядок для таблиці. */
async function pasteOneValue(page: Page): Promise<void> {
  await makeSheetEditable(page);

  const grid = page.locator('revo-grid').first();
  await expect(grid.locator('revogr-data .rgCell').first(), 'рядки сітки не завантажилися').toBeVisible({ timeout: 60_000 });
  await grid.locator('revogr-data .rgCell').first().click();
  const value = String(100 + Math.floor(Math.random() * 900));
  await page.evaluate(async (text: string) => {
    await navigator.clipboard.writeText(text);
  }, value);

  const saved = page.waitForResponse(
    (response) =>
      response.request().method() === 'PATCH' &&
      new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/cells`,
    { timeout: 90_000 },
  );
  await page.keyboard.press('Control+v');
  expect((await saved).status(), 'сервер не прийняв вставку').toBe(200);
}

/** Подає аркуш (після вставки): перевірка -> «Submit» -> 204. */
async function submitSheet(page: Page): Promise<void> {
  const validate = page.getByRole('button', { name: /Validate|Перевір/i }).first();
  if ((await validate.count()) > 0) {
    const validated = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/validate`,
      { timeout: 90_000 },
    );
    await validate.click();
    expect((await validated).status(), 'сервер не виконав перевірку').toBe(200);
    await page.waitForTimeout(1_500);
  }

  const submit = await chooseSubmitPeriod(page, Number(PeriodKey));
  const submitted = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/submit`,
    { timeout: 90_000 },
  );
  await submit.click();
  expect((await submitted).status(), 'сервер не прийняв подання').toBe(204);
  await expect(page.getByRole('tab', { selected: true }), 'аркуш не перейшов у Submitted').toContainText('Submitted', {
    timeout: 15_000,
  });
}

async function openAudit(page: Page): Promise<void> {
  await page.goto('/admin/audit');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  // Рядки журналу (якщо є) приходять після запиту; сторінка читається, коли зникли скелети.
  await expect(page.locator('[data-audit-filter-row="cells"]')).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(2_000);
}

async function openPeriods(page: Page): Promise<void> {
  await page.goto('/admin/periods');
  const overview = page.getByTestId('periods-overview');
  await expect(overview, 'немає огляду «All projects»').toBeVisible({ timeout: 30_000 });
  await expect(overview.getByRole('table')).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(1_000);
}

test.describe('uiMinorP3: дрібні пункти проходу C', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );
  test.describe.configure({ timeout: 300_000 });

  // Вподобання теми живе на сервері: наступні файли набору чекають light.
  test.afterAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '') return;
    const context = await browser.newContext();
    try {
      const page = await context.newPage();
      await signIn(page);
      await applyScheme(page, 'light');
    } finally {
      await context.close();
    }
  });

  for (const scheme of Schemes) {
    test(`uiMinorP3-assert: 500 px - сторінки документа і журналу без горизонтальної прокрутки (${scheme})`, async ({ page }) => {
      await page.setViewportSize(Wide);
      await useScheme(page, scheme);
      await signIn(page);
      await applyScheme(page, scheme);

      // Рядок журналу, щоб таблиця аудиту була в розмітці (на свіжому стенді журнал порожній).
      // ⚠ Вставка - на широкому вікні: на вузькому (≤ 640 px) сітка лише для читання (UI-42).
      await pasteOneValue(page);
      await page.setViewportSize(Narrow);

      for (let run = 1; run <= 5; run += 1) {
        await openDocument(page);
        const documentOverflow = await horizontalOverflow(page);
        expect(
          documentOverflow.scrollWidth,
          `прогін ${String(run)}/5, ${scheme}: /documents/${DocumentId} ширший за вікно ${String(documentOverflow.clientWidth)} px; найширші: ${documentOverflow.offenders.join(' | ')}`,
        ).toBeLessThanOrEqual(documentOverflow.clientWidth);

        await openAudit(page);
        const auditOverflow = await horizontalOverflow(page);
        expect(
          auditOverflow.scrollWidth,
          `прогін ${String(run)}/5, ${scheme}: /admin/audit ширший за вікно ${String(auditOverflow.clientWidth)} px; найширші: ${auditOverflow.offenders.join(' | ')}`,
        ).toBeLessThanOrEqual(auditOverflow.clientWidth);
      }
    });
  }

  test('uiMinorP3-assert: цілі ≥ 24 px на /admin/periods (CL-18, WCAG 2.5.8) - 500 і 1440', async ({ page }) => {
    await signIn(page);

    await page.setViewportSize(Narrow);
    await openPeriods(page);
    const burger = page.locator('.mantine-Burger-root');
    await expect(burger, 'на 500 px немає гамбургера').toBeVisible();
    const burgerBox = await burger.boundingBox();
    expect(burgerBox?.width ?? 0, 'ширина гамбургера').toBeGreaterThanOrEqual(24);
    expect(burgerBox?.height ?? 0, 'висота гамбургера').toBeGreaterThanOrEqual(24);

    for (const size of [Narrow, Wide]) {
      await page.setViewportSize(size);
      await openPeriods(page);

      const picks = page.locator('[data-project-pick]');
      const count = await picks.count();
      expect(count, 'в огляді немає кнопок-кодів проєктів').toBeGreaterThan(0);
      for (let index = 0; index < count; index += 1) {
        const box = await picks.nth(index).boundingBox();
        expect(box?.width ?? 0, `${String(size.width)} px: ширина кнопки проєкту #${String(index)}`).toBeGreaterThanOrEqual(24);
        expect(box?.height ?? 0, `${String(size.width)} px: висота кнопки проєкту #${String(index)}`).toBeGreaterThanOrEqual(24);
      }

      expect(await axeViolations(page, 'target-size'), `${String(size.width)} px: axe target-size на /admin/periods`).toEqual([]);
    }
  });

  test('uiMinorP3-assert: CL-22 - після Esc у «Return for edits» фокус на кнопці-відкривачі - N/5', async ({ page }) => {
    await signIn(page);
    await pasteOneValue(page);
    await submitSheet(page);

    try {
      await openDocument(page);
      const opener = page.getByRole('button', { name: /Return for edits|Повернути/i }).first();
      await expect(opener, 'у шапці немає кнопки повернення в роботу').toBeVisible({ timeout: 10_000 });

      for (let run = 1; run <= 5; run += 1) {
        // Мишею й клавіатурою по черзі: різні шляхи входу в діалог (`ReasonModal` монтується лінивим чанком).
        if (run % 2 === 1) await opener.click();
        else {
          await opener.focus();
          await page.keyboard.press('Enter');
        }

        await expect(page.getByRole('dialog'), `прогін ${String(run)}/5: діалог не відкрився`).toBeVisible({ timeout: 10_000 });
        await page.keyboard.press('Escape');
        await expect(page.getByRole('dialog'), `прогін ${String(run)}/5: Esc не закрив діалог`).toBeHidden({ timeout: 10_000 });

        await expect(opener, `прогін ${String(run)}/5: після Esc фокус не на кнопці «Return for edits»`).toBeFocused();
        expect(await page.evaluate(() => document.activeElement === document.body), `прогін ${String(run)}/5: фокус на body`).toBe(false);
      }
    } finally {
      await makeSheetEditable(page);
    }
  });

  for (const scheme of Schemes) {
    test(`uiMinorP3-shots: знімки ${scheme} (500 і 1440; документ, журнал, період)`, async ({ page }, testInfo) => {
      await useScheme(page, scheme);
      await signIn(page);
      await applyScheme(page, scheme);
      await pasteOneValue(page);

      for (const size of [Narrow, Wide]) {
        await page.setViewportSize(size);
        const tag = `${scheme}-${String(size.width)}`;

        await openDocument(page);
        await attachShot(page, testInfo, `documents-${tag}`);
        await openAudit(page);
        await attachShot(page, testInfo, `audit-${tag}`);
        await openPeriods(page);
        await attachShot(page, testInfo, `periods-${tag}`);
      }
    });
  }
});
