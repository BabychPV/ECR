import { expect, type Page } from '@playwright/test';

/**
 * Відкрити меню «More» сторінки документа і ДОВЕСТИ, що воно відкрите (`aria-expanded="true"` на кнопці).
 *
 * ⛔ Чому не просто `click()`: Playwright звіряє ціль кліку лише на ПЕРШІЙ події (`pointerdown`), а `mouseup`/`click`
 * летять у ту саму точку екрана вже без перевірки. Якщо між натиском і відпуском рядок дій зсувається (сторінка ще
 * домальовує асинхронні частини), `click` дістається спільному предкові, а не кнопці: «More» отримує фокус, але меню
 * не відкривається, і тест 45 с чекає пункт, якого немає (e2e-stand 39994116932 — `editAssignments`, 40023308269 —
 * `documentParticipantsLive`: у трасі `More [active]` без `[expanded]`, жодного кадру з меню).
 *
 * Причину в продукті прибрано (`SubmitPeriodPicker` більше не лінивий — рядок дій не переноситься після появи
 * «Подати»), але сторінка має й інші дані, що приходять після першого малюнка. Тому крок перевіряє наслідок, а
 * не таймінг: клікає лише тоді, коли меню ЗАКРИТЕ (повторний клік по відкритому закрив би його), і чекає, доки
 * кнопка скаже «відкрито». Перевірки пунктів меню (`Edit access` тощо) лишаються на викликачеві без змін.
 */
export async function openDocumentMore(page: Page): Promise<void> {
  const more = page.getByTestId('document-more');

  await expect(async () => {
    if ((await more.getAttribute('aria-expanded', { timeout: 10_000 })) !== 'true') {
      await more.click({ timeout: 10_000 });
    }
    await expect(more, 'меню «More» не відкрилось після кліку').toHaveAttribute('aria-expanded', 'true', {
      timeout: 2_000,
    });
  }).toPass({ timeout: 60_000 });
}
