import type { JSX, ReactNode } from 'react';
import { Box, Group, Stack, Text } from '@mantine/core';
import { hasContent } from './TwoLine';

/**
 * Перелік «підпис → значення» (`KIT.md` §6.7 `KeyValue`, крок `UI-04`
 * директиви №15).
 *
 * ⚠ Розмітка — справжній `<dl>`, а не сітка з `<div>`. Скрінрідер оголошує
 * список означень як «список, 6 елементів» і дозволяє стрибати між ними;
 * набір однакових `<div>` він читає суцільним текстом, у якому підпис не
 * відрізняється від значення. Шторка подробиць (`KIT.md` §3) складається
 * саме з таких переліків, тож ціна помилки тут — кожен другий екран.
 */

/**
 * Скидання власних відступів БРАУЗЕРА для `<dl>`/`<dd>`.
 *
 * ⛔ Це не вибір відступу, а зняття чужого: `dd` за замовчуванням має
 * `margin-inline-start: 40px`, `dl` — `margin-block: 1em`. Без скидання
 * значення в шторці поїхало б на 40 пікселів праворуч від свого підпису, а
 * відстань між парами задавали б ОДНОЧАСНО шкала (`gap`) і браузер — тобто
 * два джерела, з яких у токенах живе лише одне.
 *
 * ⚠ Тому це й не порушення `ФВ-14.11`: нуля в шкалі теми немає (вона
 * починається з `xs` = 4px) і бути не може, отже токеном цього не записати —
 * рівно той випадок, який коментар до самого правила називає дозволеним
 * («`height: '70vh'` … токенами не задаються і задаватися не мають»).
 */
const NoBrowserMargin = { marginBlockStart: 0, marginBlockEnd: 0 } as const;
const NoBrowserIndent = { ...NoBrowserMargin, marginInlineStart: 0 } as const;

export interface KeyValueItem {
  /** Підпис. */
  readonly label: string;

  /** Значення: текст, число або готовий вузол (напр. `StatusBadge`). */
  readonly value: ReactNode;

  /** Моноширинне значення — код, ідентифікатор, адреса комірки. */
  readonly mono?: boolean | undefined;

  /** Уточнення дрібним під значенням. */
  readonly hint?: string | undefined;
}

export interface KeyValueProps {
  readonly items: readonly KeyValueItem[];

  /** Підпис і значення в один рядок — для вузьких шторок із короткими значеннями. */
  readonly wide?: boolean | undefined;
}

/**
 * Перелік пар.
 *
 * ⛔ Пара без значення **не малюється** (`D15-06`): ні прочерку, ні порожнього
 * `<dd>`. Причина та сама, що й у директиві: «`0 зауважень` у неперевіреного
 * документа — та сама брехня, що `A7-28`». Прочерк у шторці читається як
 * «сервер відповів, і там порожньо», тоді як насправді поля може не бути у
 * відповіді взагалі — і саме цю різницю екран зобов'язаний показувати
 * відсутністю рядка.
 *
 * ⛔ Якщо значення немає в ЖОДНОЇ пари — компонент повертає `null`, а не
 * порожній `<dl>`. Порожній список означень скрінрідер оголошує («список, 0
 * елементів»), тобто привид лишається чутним, навіть коли його не видно.
 */
export function KeyValue({ items, wide = false }: KeyValueProps): JSX.Element | null {
  const visible = items.filter((item) => hasContent(item.value));

  if (visible.length === 0) return null;

  return (
    <Stack component="dl" gap={wide ? 'xs' : 'sm'} style={NoBrowserMargin} data-key-value="">
      {visible.map((item) => {
        const label = (
          <Text component="dt" size="xs" c="dimmed">
            {item.label}
          </Text>
        );

        const value = (
          <Text
            component="dd"
            size="sm"
            style={NoBrowserIndent}
            {...(item.mono === true ? { ff: 'monospace' as const } : {})}
          >
            {item.value}
          </Text>
        );

        return (
          <Box key={item.label} data-key-value-item="">
            {wide ? (
              <Group gap="sm" align="baseline" wrap="nowrap">
                {label}
                {value}
              </Group>
            ) : (
              <>
                {label}
                {value}
              </>
            )}

            {/* ⚠ Уточнення показується лише разом зі значенням: підказка до
                того, чого немає, — це та сама заглушка (`D15-06`). */}
            {item.hint !== undefined && item.hint.trim().length > 0 && (
              <Text size="xs" c="dimmed" data-key-value-hint="">
                {item.hint}
              </Text>
            )}
          </Box>
        );
      })}
    </Stack>
  );
}
