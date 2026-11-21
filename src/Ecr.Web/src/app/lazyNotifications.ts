import type { ComponentType } from 'react';

type NotificationsModule = { default: ComponentType<Record<string, unknown>> };

/**
 * Завантаження ледачого контейнера тостів (D-132) із однією повторною спробою і порожнім запасним варіантом.
 *
 * ⛔ Контейнер стоїть у корені без межі помилок: відхилений `import()` (збій мережі, розгортання між `index.html` і
 * чанком) зняв би весь корінь React — білий екран. Тости — другорядне, тож після двох невдач контейнер просто
 * не показується, а застосунок живе далі.
 */
export async function loadNotifications(importer: () => Promise<{ Notifications: unknown }>): Promise<NotificationsModule> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      return { default: (await importer()).Notifications as NotificationsModule['default'] };
    } catch {
      // друга спроба, далі — запасний варіант
    }
  }
  return { default: () => null };
}
