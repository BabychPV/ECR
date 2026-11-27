import { expect, type Page } from '@playwright/test';

/**
 * ✎ DOCUX L7: повідомлення екрана документа — рядок над сіткою (найважливіше + «+N more») і повний вміст у вкладці
 * Status інспектора. Специфікаціям, що працюють із ПОВНИМ блоком (кнопки всередині смуги виконавця, перелік рядків поділу),
 * спершу треба відкрити Status: «Details» рядка, якщо повідомлення в рядку видно, інакше «+N more».
 */
const DetailsName = /^(Details|Подробнее|Толығырақ)$/;

export async function openNoticeDetails(page: Page, rowTestId: string, timeout = 30_000): Promise<void> {
  const row = page.getByTestId(rowTestId).first();
  const more = page.getByTestId('document-notice-more');
  await expect(row.or(more).first(), `повідомлення ${rowTestId} немає ні в рядку, ні за «+N more»`).toBeVisible({ timeout });

  if (await row.isVisible()) {
    await row.getByRole('button', { name: DetailsName }).click();
  } else {
    await more.click();
  }
  await expect(page.getByTestId('document-notice-list')).toBeVisible({ timeout: 15_000 });
}
