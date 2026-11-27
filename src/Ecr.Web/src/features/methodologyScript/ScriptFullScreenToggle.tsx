import { CloseButton, Tooltip } from '@mantine/core';
import type { JSX, RefObject } from 'react';
import { t } from '@/shared/i18n';

/** Значок «розгорнути / згорнути» — той самий рисунок, що в `ResizableModal`. */
function Glyph({ full }: { readonly full: boolean }): JSX.Element {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path
        d={
          full
            ? 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5'
            : 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5'
        }
      />
    </svg>
  );
}

export interface ScriptFullScreenToggleProps {
  readonly full: boolean;
  readonly onToggle: () => void;
  readonly buttonRef: RefObject<HTMLButtonElement | null>;
}

/**
 * Кнопка «код на весь екран». Назва стала (стан несе `aria-pressed`), підказка каже, що станеться й якою клавішею.
 *
 * ⚠ `CloseButton` з власною іконкою, а не `ActionIcon` — той самий прийом, що в `ResizableModal` (без окремого чанка).
 */
export function ScriptFullScreenToggle({ full, onToggle, buttonRef }: ScriptFullScreenToggleProps): JSX.Element {
  const hint = full ? t('methodologyScript.fullScreen.exitHint') : t('methodologyScript.fullScreen.enterHint');

  return (
    <Tooltip label={hint} withArrow>
      <CloseButton
        ref={buttonRef}
        icon={<Glyph full={full} />}
        aria-label={t('methodologyScript.fullScreen.label')}
        aria-pressed={full}
        aria-keyshortcuts="Control+Shift+F"
        onClick={onToggle}
        data-testid="script-fullscreen-toggle"
      />
    </Tooltip>
  );
}
