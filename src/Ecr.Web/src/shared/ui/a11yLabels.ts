import { hasText, t } from '@/shared/i18n';

/**
 * Доступні імена службових кнопок — із каталогу, з англійським запасним
 * текстом (`X-26`).
 *
 * ⛔ Досі це були англійські ЛІТЕРАЛИ в обхід каталогу («Close notification»,
 * «Toggle password visibility», «Undo»): ключів у `09-seed.sql` не було, а
 * голий `t()` показав би читалці `⟦ключ⟧`. Тож російський і казахський
 * інтерфейс озвучував ці кнопки англійською.
 *
 * ⚠ Запасний літерал лишається — і це не половинчастість. Хрестик тоста й
 * тумблер пароля живуть і ДО завантаження каталогу (сторінка входу, тост
 * відмови самого каталогу), і там `⟦…⟧` був би гіршим за англійську.
 *
 * ⚠ Функції, а не константи: текст береться в МОМЕНТ рендера, тобто вже
 * мовою, яку людина обрала, а не мовою модуля на час імпорту.
 */

/** Хрестик сповіщення. */
export function closeNotificationLabel(): string {
  return hasText('common.closeNotification') ? t('common.closeNotification') : 'Close notification';
}

/** Дія «назад» у тості скасування. */
export function undoLabel(): string {
  return hasText('common.undo') ? t('common.undo') : 'Undo';
}

/** Тумблер видимості пароля. */
export function passwordToggleLabel(): string {
  return hasText('common.togglePasswordVisibility')
    ? t('common.togglePasswordVisibility')
    : 'Toggle password visibility';
}

/**
 * Пропи тумблера видимості пароля (`Q-260`).
 *
 * ⛔ Mantine ставить на цю кнопку `aria-hidden="true"` і `tabIndex={-1}` за
 * замовчуванням; сама наявність об'єкта знімає `aria-hidden`, а явний
 * `tabIndex: 0` повертає зупинку табом.
 */
export function passwordToggleProps(): { 'aria-label': string; tabIndex: 0 } {
  return { 'aria-label': passwordToggleLabel(), tabIndex: 0 };
}

/**
 * Пропи хрестика сповіщення для `notifications.show(...)` і `theme.ts`.
 *
 * ⚠ Геттер, а не значення: об'єкт лишається тим самим (його імпортують
 * одинадцять місць і тема), а Mantine розгортає його (`{...closeButtonProps}`)
 * під час рендера — саме тоді й читається поточний каталог.
 */
export const closeNotificationButtonProps: { readonly 'aria-label': string } = {
  get 'aria-label'(): string {
    return closeNotificationLabel();
  },
};
