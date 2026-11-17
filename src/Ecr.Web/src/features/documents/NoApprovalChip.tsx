import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { badgeLook } from '@/shared/ui/StatusBadge';
import { t } from '@/shared/i18n';

/**
 * Позначка «No approval» (B143, B75): шаблон працює без подання й затвердження, тож стан аркуша
 * (Draft/Submitted/…) тут нічого не означає — значення доступні звітності одразу після збереження, рядки
 * захищають блокування рядків (пояснення — у підказці).
 *
 * ⛔ Власна плашка, а не `StatusBadge`: «без погодження» — не стан аркуша сервера, і рядка в закритій таблиці
 * `kind × state` (`shared/ui/**`) для нього немає; невідомий стан там малюється «увагою». Вигляд — ті самі
 * токени нейтрального тону (`badgeLook`), що й у чипа стану, який вона заміняє (той самий прийом —
 * `AssignmentBadge`, `PermitStateBadge`). Зміст несе слово; риска — декоративна.
 *
 * ✎ B147: винесено з `DocumentProgress` — ту саму плашку покажуть дошка й швидкий перегляд переліку.
 */
export function NoApprovalChip(): JSX.Element {
  const look = badgeLook('neutral');

  return (
    <Badge
      size="sm"
      variant="transparent"
      miw="fit-content"
      radius="xs"
      c={look.text}
      bg={look.bg}
      leftSection={
        <svg
          width={12}
          height={12}
          viewBox="0 0 24 24"
          fill="none"
          stroke={look.icon}
          strokeWidth={2}
          strokeLinecap="round"
          aria-hidden="true"
          focusable="false"
          style={{ flex: 'none', display: 'block' }}
        >
          <path d="M5 12h14" />
        </svg>
      }
      styles={{
        root: {
          height: 20,
          paddingInline: '4px var(--mantine-spacing-xs)',
          border: `1px solid ${look.border}`,
          textTransform: 'none',
          letterSpacing: 'normal',
          fontWeight: 500,
          fontSize: 'var(--mantine-font-size-xs)',
          gap: 4,
        },
        section: { marginInlineEnd: 0 },
      }}
      style={{ width: 'max-content' }}
      title={t('workflow.noApprovalHint')}
      data-testid="document-no-approval"
      data-status-tone="neutral"
    >
      {t('workflow.noApproval')}
    </Badge>
  );
}

