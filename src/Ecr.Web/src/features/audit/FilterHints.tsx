import type { CSSProperties, JSX } from 'react';
import { Stack, Text } from '@mantine/core';

/**
 * Пояснення до полів ряду фільтрів — ПІД рядом, а не під підписом поля (`U-21`).
 *
 * ⛔ Чому не видимий `description` у самих полях. Ряд вирівняний `align="end"`,
 * і поле з поясненням під підписом вище за поле без нього: у журналі комірок
 * два поля з чотирьох мали пояснення, два — ні, тож підписи стояли на двох
 * рівнях, і ряд читався як зсунутий. Дописати пояснення всім означало б
 * вигадати тексти, яким нема чого сказати («Origin» пояснення не потребує), і
 * завести нові ключі в `09-seed.sql`; перенести пояснення під поле — розсунуло
 * б уже самі поля. Тому ВИДИМА будова всіх полів однакова — «підпис + поле», а
 * видимий текст пояснень живе тут.
 *
 * ⛔ Чому `description` при цьому ЛИШАЄТЬСЯ в полі, лише прихований для ока.
 * Перша спроба — власний `aria-describedby` на полі — тихо не працювала:
 * Mantine (`Input.mjs`) розгортає свій `aria-describedby` ПІСЛЯ пропсів
 * викликача, і той, коли `description`/`error` немає, дорівнює `undefined`.
 * Тобто пояснення зникало для читалки без жодної помилки. `description` —
 * єдиний шлях, яким Mantine сам зв'язує пояснення з полем.
 *
 * ⚠ Тому цей блок `aria-hidden`: читалка вже отримує той самий текст через
 * поле, і без цього читала б його двічі. Обидва тексти — з одного ключа
 * каталогу, розійтися їм нема де.
 *
 * ⚠ `c="dimmed"` — токен теми, а не зашитий колір: однаково читається в обох
 * темах (гейти `a11y (dark)`/`a11y (light)`).
 */
export function FilterHints({ texts }: { readonly texts: readonly string[] }): JSX.Element | null {
  if (texts.length === 0) return null;

  return (
    <Stack gap="xs" mb="md" aria-hidden="true" data-filter-hints="true">
      {texts.map((text) => (
        <Text key={text} size="xs" c="dimmed">
          {text}
        </Text>
      ))}
    </Stack>
  );
}

/**
 * Стиль, що ховає `description` поля від ока, але не від читалки.
 *
 * ⚠ Той самий прийом, що в `VisuallyHidden` Mantine: елемент поза потоком, тож
 * він не додає висоти над полем — а саме ця висота й зсувала підписи.
 * Передається як `styles={readerOnlyDescription}`.
 */
const visuallyHidden: CSSProperties = {
  position: 'absolute',
  width: 1,
  height: 1,
  margin: -1,
  padding: 0,
  border: 0,
  overflow: 'hidden',
  clip: 'rect(0 0 0 0)',
  whiteSpace: 'nowrap',
};

export const readerOnlyDescription = { description: visuallyHidden } as const;
