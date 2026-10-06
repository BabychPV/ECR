import { expect, test, type Page } from '@playwright/test';
import { expectFocusTrapped, focusState } from './focus';

/**
 * `UI-29`: швидкий перегляд документа зі списку — шторка `?panel=<id>`.
 *
 * ⚠ Навіщо прогін у браузері, якщо є `DocumentsPage.quickLook.test.tsx`: шторка
 * монтується лінивим чанком уже відкритою, і Mantine `returnFocus` у такому разі не
 * бачить переходу «закрито → відкрито» — опенера не запам'ятовує. Повернення фокуса
 * робить сторінка сама (`quickLookOpener`); тут — що воно дійшло до справжньої збірки
 * Vite зі справжніми кадрами переходу.
 *
 * ⛔ Мутація (лише локально, jsdom): без `opener?.focus()` у `onClose` тест Esc червоний.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

/** Стенд позначається тими ж змінними, що й решта прогонів (`tools/e2e-stand.ps1`). */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Документи: швидкий перегляд', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('модальний діалог: шторка швидкого перегляду тримає фокус, Escape закриває й вертає фокус на «око»', async ({
    page,
  }) => {
    await signIn(page, Operator.user, Operator.password);
    await page.goto(`/?periodKey=${PeriodKey}`);

    const eye = page.locator(`[data-quick-look="${DocumentId}"]`);
    await expect(eye, 'у рядку документа немає кнопки швидкого перегляду').toBeVisible({ timeout: 30_000 });

    // Клавіатурою: людина без миші мусить і відкрити, і закрити шторку.
    await eye.focus();
    const opener = await focusState(page);
    await page.keyboard.press('Enter');

    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 10_000 });
    await expect(page).toHaveURL(new RegExp(`[?&]panel=${DocumentId}(&|$)`));
    await expect(
      drawer.getByRole('button', { name: /Open document|Відкрити документ|Открыть документ/i }),
    ).toBeVisible({ timeout: 10_000 });
    await expectFocusTrapped(page, '[role="dialog"]');

    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden({ timeout: 10_000 });
    await expect(page, 'Escape не прибрав ?panel= з адреси').not.toHaveURL(/[?&]panel=/);

    const returned = await focusState(page);
    expect(returned.tag, 'після Escape фокус на body').not.toBe('body');
    expect(returned.label, 'після Escape фокус не повернувся на «око», яке відкрило шторку').toBe(opener.label);
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
