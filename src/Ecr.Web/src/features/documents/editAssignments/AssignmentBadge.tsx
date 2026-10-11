import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { t } from '@/shared/i18n';
import { badgeLook, type StatusTone } from '@/shared/ui/StatusBadge';

/**
 * Статус передачі / виконавця.
 *
 * ⛔ Власний бейдж, а не `StatusBadge`: таблиця `kind × state` там закрита й у `shared/ui/**`
 * (EA-5a не чіпає спільне). Вигляд — ті самі токени `badgeLook`. Статус названий СЛОВОМ і
 * піктограмою-формою (`aria-hidden`), не лише кольором. Зелений не вживається (KIT §1.3):
 * «Валідовано» нейтральне, «Заповнено» — `info` (чекає дії власника), «Повернено» — `warning`.
 */
const Tone: Readonly<Record<string, StatusTone>> = {
  Open: 'muted',
  Assigned: 'neutral',
  InProgress: 'neutral',
  Filled: 'info',
  Validated: 'neutral',
  Returned: 'warning',
};

const Glyph: Readonly<Record<string, string>> = {
  Open: '○',
  Assigned: '◔',
  InProgress: '◑',
  Filled: '●',
  Validated: '✓',
  Returned: '↩',
};

function label(state: string): string | null {
  switch (state) {
    case 'Open':
      return t('editAssign.status.Open');
    case 'Assigned':
      return t('editAssign.status.Assigned');
    case 'InProgress':
      return t('editAssign.status.InProgress');
    case 'Filled':
      return t('editAssign.status.Filled');
    case 'Validated':
      return t('editAssign.status.Validated');
    case 'Returned':
      return t('editAssign.status.Returned');
    default:
      return null;
  }
}

export function AssignmentBadge({ state }: { readonly state: string }): JSX.Element {
  const known = Tone[state] !== undefined;
  // Невідомий стан сервера — «увага» з сирим кодом, як в `StatusBadge`: мовчки сірим він не стає.
  const tone: StatusTone = known ? (Tone[state] as StatusTone) : 'warning';
  const look = badgeLook(tone);

  return (
    <Badge
      size="sm"
      variant="transparent"
      radius="xs"
      c={look.text}
      bg={look.bg}
      leftSection={<span aria-hidden="true">{Glyph[state] ?? '!'}</span>}
      styles={{
        root: {
          height: 20,
          border: `1px solid ${look.border}`,
          textTransform: 'none',
          letterSpacing: 'normal',
          fontWeight: 500,
          fontSize: 'var(--mantine-font-size-xs)',
          width: 'max-content',
        },
        section: { color: look.icon },
      }}
      data-assignment-status={state}
    >
      {label(state) ?? state}
    </Badge>
  );
}
