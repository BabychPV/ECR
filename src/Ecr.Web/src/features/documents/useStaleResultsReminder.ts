import { useEffect, useRef } from 'react';
import { notifications } from '@mantine/notifications';
import { closeNotificationButtonProps } from '@/shared/ui/a11yLabels';
import { t } from '@/shared/i18n';

/**
 * Нагадування при виході з документа, де результати застаріли, а «Перерахувати» не натиснули (рішення людини:
 * автоперерахунку немає - система пам'ятає й нагадує; сам перелік - блок «Потребують перерахунку» у «My tasks»).
 *
 * ⛔ Тост лише тому, хто МОЖЕ перерахувати (`canRecalculate`): інакше нагадування про дію, якої роль не має.
 * ⚠ Спрацьовує на розмонтуванні сторінки/панелі, не на зміні аркуша чи періоду всередині документа. Значення
 * читаються з `ref` на момент виходу, а не з замикання першого рендеру.
 */
export function useStaleResultsReminder(documentId: number, stale: boolean, canRecalculate: boolean): void {
  const latest = useRef({ stale, canRecalculate });
  useEffect(() => {
    latest.current = { stale, canRecalculate };
  }, [stale, canRecalculate]);

  useEffect(
    () => () => {
      if (!latest.current.stale || !latest.current.canRecalculate) return;

      notifications.show({
        id: `stale-results-${String(documentId)}`,
        color: 'statusWarning',
        message: t('document.staleResults.leaveReminder', { id: documentId }),
        closeButtonProps: closeNotificationButtonProps,
      });
    },
    [documentId],
  );
}
