import { notifications } from '@mantine/notifications';
import { t } from '@/shared/i18n';
import { flushUnsaved, hasUnsavedChanges } from '@/shared/ui/unsavedSources';

/**
 * AN-28 / L8-01: дії над документом (Submit, Approve/Reject, Validate, Export,
 * Import Apply, Recalculate, міграція версії) спершу зберігають те, що оператор
 * щойно набрав у сітці.
 *
 * Дефект: ці кнопки НЕ викликали `flushUnsaved`. Набрав значення -> одразу
 * Submit: POST /submit ішов раніше за PATCH (дебаунс 500 мс + закриття
 * редактора на наступному кадрі), сервер подавав без значення, а пізніший
 * PATCH отримував 403 `DocumentSubmitted` - значення втрачалося мовчки.
 */

/**
 * Чекає, доки RevoGrid зафіксує відкритий редактор (кадр + тік), і зберігає
 * все. `false` - частину зберегти не вдалося (відмова сервера, конфлікт, тайм-аут).
 *
 * ⚠ Запасний тайм-аут, бо `requestAnimationFrame` не спрацьовує у фоновій
 * вкладці: без нього дія зависала б до повернення на вкладку.
 */
export async function settleAndFlushEdits(): Promise<boolean> {
  await new Promise<void>((resolve) => {
    const fallback = setTimeout(resolve, 100);
    requestAnimationFrame(() => {
      clearTimeout(fallback);
      setTimeout(resolve, 0);
    });
  });

  return hasUnsavedChanges() ? flushUnsaved() : true;
}

/**
 * Виконує `action` ЛИШЕ після успішного збереження незбереженого. Якщо
 * зберегти не вдалося - дія не виконується, оператор бачить пояснення (а самі
 * правки лишаються в сітці й показують свою відмову).
 */
export async function whenEditsSaved(action: () => void): Promise<void> {
  if (await settleAndFlushEdits()) {
    action();
    return;
  }

  notifications.show({ color: 'statusError', message: t('document.unsavedBlocksAction') });
}
