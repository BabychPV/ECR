import type { JSX } from 'react';

/**
 * B145: заглушки `ConfirmModal` / `ReasonModal`, чия кнопка підтвердження є в DOM ЗАВЖДИ.
 *
 * ⚠ Навіщо. Справжній `Modal` після `opened = false` лишається в DOM ще ~200 мс (анімація закриття) і
 * зникає за таймером — тест «клік по діалогу, що закривається» на справжньому діалозі залежав би від
 * часу. Заглушка прибирає час із доказу: кнопка на місці, а чи діалог відкритий, каже `data-opened`.
 *
 * Підключення (фабрика `vi.mock` піднімається над імпортами, тому — динамічний імпорт):
 *   vi.mock('@/shared/ui/ConfirmModal', async () => ({ ConfirmModal: (await import('@/test/alwaysMountedDialogs')).ConfirmModal }));
 *   vi.mock('@/shared/ui/ReasonModal', async () => ({ ReasonModal: (await import('@/test/alwaysMountedDialogs')).ReasonModal }));
 */

/** Причина, яку «вводить» заглушка `ReasonModal`. */
export const StubReason = 'причина з тесту';

export function ConfirmModal(props: {
  readonly opened: boolean;
  readonly verb: string;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: () => void;
}): JSX.Element {
  return (
    <button
      type="button"
      data-opened={String(props.opened)}
      data-pending={String(props.isPending === true)}
      onClick={props.onConfirm}
    >
      {`confirm:${props.verb}`}
    </button>
  );
}

export function ReasonModal(props: {
  readonly opened: boolean;
  readonly confirmLabel: string;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: (reason: string) => void;
}): JSX.Element {
  return (
    <button
      type="button"
      data-opened={String(props.opened)}
      data-pending={String(props.isPending === true)}
      onClick={() => props.onConfirm(StubReason)}
    >
      {`reason:${props.confirmLabel}`}
    </button>
  );
}
