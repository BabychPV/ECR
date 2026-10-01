import type { JSX } from 'react';
import { Group, Loader, Text } from '@mantine/core';
import type { DurationPhase } from './useDurationIndicator';

/** Властивості показу прогресу. */
interface DurationProgressProps {
  /** Фаза з `useDurationIndicator`. */
  phase: DurationPhase;
  /** Що саме триває — уже перекладений текст (`t('…')`). */
  label: string;
}

/**
 * Прогрес із текстом для дій `1–10 с` (`ФВ-14.26`).
 *
 * Показується лише у фазі `progress`; у `none` і `inline` не займає місця —
 * там достатньо стану самого елемента керування.
 *
 * ⚠ Відсотка тут немає свідомо: синхронний запит не повідомляє, скільки
 * лишилось, а вигаданий відсоток бреше. Справжній прогрес (`ФВ-14.8`) — у
 * фонових задачах («Мої задачі»).
 *
 * ⚠ `role="status"`: текст оголошується читалкою, не перехоплюючи фокус.
 */
export function DurationProgress({ phase, label }: DurationProgressProps): JSX.Element | null {
  if (phase !== 'progress') return null;

  return (
    <Group gap="xs" wrap="nowrap" role="status" aria-live="polite" data-testid="duration-progress">
      <Loader size="xs" aria-hidden="true" />
      <Text size="sm">{label}</Text>
    </Group>
  );
}
