import type { JSX, ReactNode } from 'react';
import { Box, Text } from '@mantine/core';

/**
 * Два рядки в одній комірці: людський підпис і код під ним
 * (`KIT.md` §6.4 `twoLine`, крок `UI-04` директиви №15).
 *
 * ⚠ Навіщо це окремий компонент, а не `<div>` із двох `<Text>`: правило
 * `KIT.md` §1.7 — «людські підписи замість кодів, код приглушено другим
 * рядком» — стосується кожної таблиці переліку, а таких екранів дванадцять.
 * Дванадцять власних `<div>` розійдуться на дванадцять різних розмірів
 * приглушеного тексту, і привести їх назад коштує дорожче, ніж завести одне
 * місце (та сама причина, що й у `ФВ-14.11` для кольору).
 */

/**
 * Чи є в значенні що показувати (`D15-06`).
 *
 * ⛔ `D15-06` дослівно: «Елемент, для якого немає даних, **не малюється** (ні
 * заглушки, ні фейкового числа)». Тому порожнє значення — це не «—» і не
 * порожній вузол-привид: рядок просто відсутній у розмітці. Прочерк — це
 * теж заглушка, просто коротка: він займає місце, читається скрінрідером і
 * не відрізняється від «справді порожньо» у даних.
 *
 * ⚠ `0` і `false` — це ДАНІ, а не порожнеча: `0 зауважень` — змістовна
 * відповідь. Порожнім вважається лише `null`, `undefined`, порожній або
 * пробільний рядок і порожній масив вузлів.
 *
 * ⚠ Предикат живе тут, а не в окремому файлі: `TwoLine` — примітив одного
 * рядка, а `KeyValue` складається саме з таких рядків і імпортує його
 * звідси. Заводити п'ятий файл під тризначну функцію картка `UI-04` не
 * передбачає.
 */
export function hasContent(value: ReactNode): boolean {
  if (value === null || value === undefined || value === false) return false;
  if (typeof value === 'string') return value.trim().length > 0;
  if (Array.isArray(value)) return value.some((item: ReactNode) => hasContent(item));

  return true;
}

export interface TwoLineProps {
  /** Верхній рядок — людський підпис. */
  readonly primary?: ReactNode;

  /** Нижній рядок — код або уточнення, приглушено. */
  readonly secondary?: ReactNode;

  /** Моноширинний нижній рядок (код, адреса комірки). */
  readonly mono?: boolean | undefined;

  /** Підказка при наведенні — зазвичай повний текст, що не вміщається. */
  readonly title?: string | undefined;
}

/**
 * Підпис і код під ним.
 *
 * ⛔ Порожній рядок не малюється взагалі (`D15-06`, див. `hasContent`): якщо
 * коду немає — лишається сам підпис без порожнього місця під ним; якщо немає
 * нічого — компонент повертає `null`, а не порожній `<div>`. Порожній вузол
 * у таблиці не нейтральний: він розтягує рядок і залишає скрінрідерові
 * беззмістовну зупинку.
 */
export function TwoLine({ primary, secondary, mono = false, title }: TwoLineProps): JSX.Element | null {
  const showPrimary = hasContent(primary);
  const showSecondary = hasContent(secondary);

  if (!showPrimary && !showSecondary) return null;

  return (
    <Box title={title} data-two-line="">
      {showPrimary && (
        <Text size="sm" data-two-line-primary="">
          {primary}
        </Text>
      )}
      {showSecondary && (
        <Text
          size="xs"
          c="dimmed"
          data-two-line-secondary=""
          {...(mono ? { ff: 'monospace' as const } : {})}
        >
          {secondary}
        </Text>
      )}
    </Box>
  );
}
