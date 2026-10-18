import type { CSSProperties, JSX } from 'react';
import { Badge } from '@mantine/core';
import { badgeLook } from '@/shared/ui/StatusBadge';
import { permitStateLabel, permitStateTone } from '../shell/permitStatus';

/**
 * Бейдж стану перміта (Draft / Active / Expired). Вигляд — той самий, що в `StatusBadge`
 * (токени тону через `badgeLook`), підпис — власний (`permitStatus.ts` пояснює, чому не
 * `StatusBadge kind="version"`). Підпис словом, а не лише кольором (`ФВ-14.18`).
 */
export function PermitStateBadge({ state }: { readonly state: string }): JSX.Element {
  const tone = permitStateTone(state);
  const look = badgeLook(tone);

  return (
    <Badge
      size="sm"
      variant="transparent"
      miw="fit-content"
      radius="xs"
      c={look.text}
      bg={look.bg}
      styles={{
        root: {
          height: 20,
          paddingInline: 'var(--mantine-spacing-xs)',
          border: `1px solid ${look.border}`,
          textTransform: 'none',
          letterSpacing: 'normal',
          fontWeight: 500,
          fontSize: 'var(--mantine-font-size-xs)',
        },
      }}
      style={{ width: 'max-content' } as CSSProperties}
      data-permit-state={state}
      data-status-tone={tone}
    >
      {permitStateLabel(state)}
    </Badge>
  );
}
