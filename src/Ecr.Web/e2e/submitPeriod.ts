import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Кнопка «Подати: <період>», налаштована на ВКАЗАНИЙ період (D-441, AN-128).
 *
 * ⛔ З D-441 «Подати» подає НАЙРАНІШИЙ неподаний період у стані Open/Grace, а не відкритий на екрані:
 * на стенді, коли поточний місяць Open, а попередній ще в Grace (політика `ECR-Standard`: Grace з 15-го дня
 * після кінця місяця до 45-го), кнопка подала б попередній місяць. Прогони, що перевіряють подання
 * ВІДКРИТОГО періоду, вибирають його явно — тим самим вибором ▾, яким користується оператор.
 *
 * @param page Сторінка документа.
 * @param periodKey Період, який має подати кнопка.
 * @param options `keyboard` — вибір лише клавіатурою (прохід `keyboardPath.spec.ts`).
 * @returns Кнопка подання, що подасть `periodKey`.
 */
export async function chooseSubmitPeriod(
  page: Page,
  periodKey: number,
  options: { readonly keyboard?: boolean } = {},
): Promise<Locator> {
  const submit = page.getByTestId('document-submit');
  await expect(submit, 'у шапці немає кнопки подання').toBeVisible({ timeout: 30_000 });

  const wanted = String(periodKey);
  if ((await submit.getAttribute('data-period-key')) === wanted) return submit;

  const pick = page.getByTestId('submit-period-pick');
  const item = page.locator(`[data-submit-period="${wanted}"]`);
  await expect(pick, `період ${wanted} не серед доступних для подання`).toBeVisible({ timeout: 10_000 });

  if (options.keyboard === true) {
    await pick.focus();
    await page.keyboard.press('Enter');
    await expect(item, 'меню вибору періоду не відкрилося з клавіатури').toBeVisible({ timeout: 10_000 });
    // ⚠ Фокус на пункт — так само програмно, як `expectFocusRing` ставить його на кнопки проходу;
    // активація — клавішею.
    await item.focus();
    await expect(item, 'пункт періоду не приймає фокус').toBeFocused();
    await page.keyboard.press('Enter');
  } else {
    await pick.click();
    await item.click();
  }

  await expect(submit, 'кнопка не перейшла на вибраний період').toHaveAttribute('data-period-key', wanted);

  return submit;
}

/**
 * ✎ UAT-p3 №1: «Подати: <місяць>» спершу питає (як Approve / Return for edits) — підтвердження в діалозі.
 *
 * @param page Сторінка документа.
 * @param options `keyboard` — фокус на «Submit» діалогу й Enter (прохід `keyboardPath.spec.ts`).
 * @returns Діалог підтвердження (уже закривається).
 */
export async function confirmSubmit(page: Page, options: { readonly keyboard?: boolean } = {}): Promise<Locator> {
  const dialog = page.getByRole('dialog');
  await expect(dialog, 'подання не спитало підтвердження').toBeVisible({ timeout: 15_000 });
  const confirm = dialog.getByRole('button', { name: /^(Submit|Подать|Подати|Тапсыру)$/ });

  if (options.keyboard === true) {
    await confirm.focus();
    await page.keyboard.press('Enter');
  } else {
    await confirm.click();
  }

  return dialog;
}

/** Клік «Подати: <місяць>» і підтвердження в діалозі (UAT-p3 №1). */
export async function submitAndConfirm(page: Page, submit: Locator): Promise<void> {
  await submit.click();
  await confirmSubmit(page);
}
