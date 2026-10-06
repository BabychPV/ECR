import { expect, test, type Page } from '@playwright/test';

/**
 * Повторне відкриття діалогу одразу після закриття не перемонтовує його вміст.
 *
 * ⛔ Дефект Mantine 7.15.2 (`Transition/use-transition`): нове відкриття скасовувало лише
 * таймер переходу, але не запланований `requestAnimationFrame` закриття. Закрили й відкрили
 * протягом двох кадрів — таймер «exited» закриття доходив до кінця вже ПІСЛЯ відкриття:
 * вміст розмонтовувався, і за мить монтувався знову. Поле, у яке вже друкують,
 * відʼєднувалось разом із фокусом. Фікс — `scripts/patch-mantine-transition.mjs`.
 *
 * ⚠ Навіщо прогін у браузері, якщо є `modalReopenRemount.test.tsx`: той доводить латку на
 * голому `Modal`/`Drawer` у jsdom з імітацією кадрів. Тут — справжні кадри, справжня
 * збірка Vite і справжній лінивий діалог продукту («New project» на `/admin/periods`,
 * `CreateProjectModal`), тобто що латка дійшла туди, де її бачить людина.
 *
 * ⚠ Ознака перемонтування — позначка на DOM-вузлі поля, а не значення: значення коду живе
 * в стані над `Modal` і пережило б перемонтування, а `data-autofocus` повернув би фокус у
 * нове поле — дефект сховався б саме від перевірки значення.
 *
 * ⛔ Мутація (лише локально, у Chromium на голому `Modal` з темою продукту й тими самими
 * кроками й позначкою): без латки в `node_modules` — 10/10 червоних (вузол замінено, поле
 * порожнє), з латкою — 10/10 зелених.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

/** Стенд позначається тими ж змінними, що й решта прогонів (`tools/e2e-stand.ps1`). */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Діалоги: повторне відкриття', () => {
  // ⚠ Без стенда падає весь набір (`e2e/globalSetup.ts`); пропуск — лише під
  // `ECR_E2E_OPTIONAL`, як у сусідніх файлах.
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('модальний діалог: закрити й одразу відкрити — форма не перемонтовується', async ({ page }) => {
    await signIn(page, Operator.user, Operator.password);
    await page.goto('/admin/periods');

    const opener = page.getByRole('button', { name: /New project|Новий проєкт/i }).first();
    await expect(opener, 'немає кнопки створення проєкту').toBeVisible({ timeout: 30_000 });
    await opener.click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    // Більше за тривалість переходу (150 мс): діалог повністю відкритий, лінивий модуль завантажено.
    await page.waitForTimeout(500);

    /*
     * ⛔ Закриття і повторне відкриття — в ОДНОМУ виклику сторінки, через кадр одне від
     * одного: саме це вікно (до другого кадру ланцюжка закриття) і відкривав дефект.
     * Дві окремі дії Playwright ідуть через протокол браузера й могли б розминутися з ним —
     * тоді прогін зеленів би й без латки.
     */
    await opener.evaluate((button) => {
      button.setAttribute('data-e2e-opener', '');
      const close = document.querySelector<HTMLElement>('[role="dialog"] .mantine-Modal-close');
      if (close === null) throw new Error('у діалозі немає кнопки закриття');
      close.click();
      requestAnimationFrame(() => document.querySelector<HTMLElement>('[data-e2e-opener]')?.click());
    });

    // Кілька кадрів — щоб пастка фокуса поставила фокус, але менше за тривалість переходу:
    // дефектний таймер «exited» ще попереду.
    await page.waitForTimeout(60);
    const code = dialog.getByRole('textbox').first();
    await code.evaluate((input) => {
      (input as HTMLInputElement & { e2eMark?: true }).e2eMark = true;
    });
    await code.pressSequentially('TOLUENE');
    await page.waitForTimeout(500);

    await expect(dialog, 'діалог закрився замість повторного відкриття').toBeVisible();
    const sameNode = await code.evaluate(
      (input) => (input as HTMLInputElement & { e2eMark?: true }).e2eMark === true && input.isConnected,
    );
    expect(sameNode, 'вміст діалогу перемонтовано: поле, у яке друкували, замінене новим').toBe(true);
    await expect(code).toHaveValue('TOLUENE');

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden({ timeout: 10_000 });
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);
  // ⚠ Роль, а не підпис: `getByLabel(/Password|Пароль/i)` резолвиться і в кнопку-тумблер
  // видимості пароля (`LoginPage.tsx`, Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
