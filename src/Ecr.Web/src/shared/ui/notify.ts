import { notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';

/**
 * Доступне ім'я хрестика на сповіщенні (UI-прохід, F8).
 *
 * ⛔ Mantine 7 малює цю кнопку через `<CloseButton>` БЕЗ тексту й без
 * `aria-label` (`Notification.mjs`: `withCloseButton && <CloseButton iconSize
 * … {...closeButtonProps} />`), тобто читалка оголошує її просто «кнопка».
 * На відміну від стрілок лічильника `NumberInput` (яким Mantine сам ставить
 * `aria-hidden`, тобто з дерева доступності їх прибрано), хрестик тоста
 * ДОСЯЖНИЙ — і це єдина дія, якою сповіщення можна прибрати з екрана.
 *
 * ⚠ Напис — ЛІТЕРАЛ, не `t()`, з тієї ж причини, що `passwordToggleProps` у
 * `pages/LoginPage.tsx`: рядки цього застосунку йдуть із серверного каталогу
 * (`09-seed.sql`), ключа під цей напис там ще немає, а голий `t()` без рядка
 * показав би читалці позначений ключ (`⟦…⟧`) замість опису кнопки.
 *
 * ⚠ Експортується навмисно: `notifications.show(...)` кличуть і повз цей
 * модуль (`features/workflow/SheetActions.tsx`, `pages/DocumentPage.tsx`,
 * `features/export/ExportButton.tsx`, `features/security/UserAccessEditor.tsx`,
 * `features/mapping/CreateMappingModal.tsx`, `pages/admin/SourcesPage.tsx`,
 * `pages/admin/PeriodsPage.tsx`, `pages/admin/SnapshotsPage.tsx`,
 * `pages/admin/GrantsPanel.tsx`), і другий літерал у кожному з них розійшовся
 * б із цим непомітно.
 */
export const notificationCloseButtonProps = { 'aria-label': 'Close notification' } as const;

/**
 * Показує причину відмови так, як її назвав сервер.
 *
 * ⛔ Саме `error.message`, а не «не вдалося». Відмови цієї системи змістовні:
 * «період закрито — спершу відкрийте період» (`ECR-PRD-4223`), «затвердження
 * відхилено: маршрут не містить вашої ролі» (`ECR-ACCS-0403`), «коментар при
 * відхиленні обов'язковий» (`ECR-DOC-0422`). Замінити їх на «щось пішло не
 * так» означає викинути єдину підказку, яка веде до дії (`ФВ-14.24`).
 *
 * ⚠ Функція існує тому, що цей самий блок був скопійований у кожному екрані.
 * Скопійований — означає, що в одному з них рано чи пізно лишиться `String(error)`
 * без розбору, і саме там відмова стане німою.
 */
export function showApiError(error: unknown): void {
  notifications.show({
    color: 'statusError',
    message: error instanceof EcrApiError ? error.message : String(error),
    closeButtonProps: notificationCloseButtonProps,
  });
}

/** Показує підтвердження успішної дії. */
export function showDone(message: string): void {
  notifications.show({
    color: 'statusSuccess',
    message,
    closeButtonProps: notificationCloseButtonProps,
  });
}
