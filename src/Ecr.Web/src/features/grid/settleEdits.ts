import { notifications } from '@mantine/notifications';
import { t } from '@/shared/i18n';
import { flushUnsaved, hasUnsavedChanges } from '@/shared/ui/unsavedSources';
import { firstHeldEdit, type HeldEdit } from './pendingStore';

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
 * Хто вміє показати утриману комірку: перемкнути аркуш, прокрутити, підсвітити.
 *
 * ⚠ Реєструє сторінка документа - лише вона знає аркуші й таблиці; кнопки дій
 * (`SheetActions`, `ExportButton`, `ImportPanel`, ...) про них не знають.
 */
type HeldEditRevealer = (held: HeldEdit) => void;

let revealer: HeldEditRevealer | null = null;

/** Ставить показ утриманої комірки; повертає зняття (лише свого). */
export function registerHeldEditRevealer(reveal: HeldEditRevealer): () => void {
  revealer = reveal;

  return () => {
    if (revealer === reveal) revealer = null;
  };
}

export interface WhenEditsSavedOptions {
  /**
   * Дія лише ЧИТАЄ збережене (Validate, Export): незбережене її не блокує -
   * вона йде по збереженому, а оператор бачить попередження (AN-28 P2-1).
   * Блокуються лише дії, що змінюють стан чи дані: там утрачене значення
   * подалося б без нього.
   */
  readonly readOnly?: boolean;
}

/**
 * Пояснює, чому незбережене не збереглося.
 *
 * ⛔ AN-28 P2-1: раніше - один загальний тост через 3 с тиші. Тепер утримана
 * (відхилена сервером) правка називається одразу, з причиною сервера, а сама
 * комірка показується (`revealer`): інакше оператор шукав би її по 91 таблиці.
 */
function explainUnsaved(options: WhenEditsSavedOptions): void {
  const held = firstHeldEdit();

  if (options.readOnly === true) {
    notifications.show({ color: 'statusWarning', message: t('document.unsavedNotIncluded') });
  } else if (held === null) {
    notifications.show({ color: 'statusError', message: t('document.unsavedBlocksAction') });
  } else {
    notifications.show({
      color: 'statusError',
      title: t('document.heldEditBlocksAction'),
      message: held.message,
    });
  }

  if (held !== null) revealer?.(held);
}

/**
 * Виконує `action` ЛИШЕ після успішного збереження незбереженого. Якщо
 * зберегти не вдалося - дія не виконується (крім `readOnly`), оператор бачить
 * причину, а утримана комірка - підсвічена.
 *
 * @returns Чи виконано `action`.
 */
export async function whenEditsSaved(
  action: () => void,
  options: WhenEditsSavedOptions = {},
): Promise<boolean> {
  const saved = await settleAndFlushEdits();

  if (!saved) explainUnsaved(options);

  if (saved || options.readOnly === true) {
    action();

    return true;
  }

  return false;
}
