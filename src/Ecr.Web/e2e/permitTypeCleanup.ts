import type { Page } from '@playwright/test';

/**
 * Прибирання типів дозволів, заведених e2e-прогоном (аудит ч.1, P3-5). Тест кладе код у `track()` ДО
 * першого запиту, що може його створити, а `afterEach` кличе `cleanup()`: так тип зникає й тоді, коли
 * тест упав посередині.
 *
 * `DELETE /api/v1/permit-types/{code}` видаляє лише чернетку, що ніколи не публікувалась: опублікований тип
 * сервер не віддає (`ECR-PRM-4094`), а вже видалений — `404`. Обидві відповіді тут не помилка (тест міг
 * сам опублікувати чи видалити тип); інше — так, бо тоді стенд лишається забрудненим, і це має бути видно.
 *
 * Імена e2e-типів починаються з `E2E_` (код із назви губить підкреслення, тож `E2E_` у назві — це про
 * людину, що дивиться на перелік).
 */
export class CreatedPermitTypes {
  private readonly codes = new Set<string>();

  track(code: string): string {
    this.codes.add(code);
    return code;
  }

  async cleanup(page: Page): Promise<void> {
    const failed: string[] = [];
    for (const code of this.codes) {
      const removed = await page.request.delete(`/api/v1/permit-types/${encodeURIComponent(code)}`);
      if (removed.status() < 300 || removed.status() === 404 || removed.status() === 409) continue;
      failed.push(`${code}: ${removed.status()} ${await removed.text()}`);
    }
    this.codes.clear();
    if (failed.length > 0) throw new Error(`e2e: типи не прибрано: ${failed.join('; ')}`);
  }
}
