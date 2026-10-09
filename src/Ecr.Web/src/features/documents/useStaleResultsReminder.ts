import { useEffect, useRef, type RefObject } from 'react';
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
 *
 * ⛔ N3-08: рядок дій (`DocumentActionBar`) стоїть під `AsyncBoundary` сторінки, а той на зміну ПЕРІОДУ
 * (новий ключ запиту `summary`) розмонтовує вміст - і хук у рядку нагадував про «вихід» посеред документа.
 * Тому власник нагадування - сама сторінка (`useStaleResultsReminderHost`, живе, доки відкритий документ), а рядок
 * лише звітує їй стан (`host` четвертим аргументом).
 */
export interface StaleReminderState {
  readonly stale: boolean;
  readonly canRecalculate: boolean;
}

const NothingToRemind: StaleReminderState = { stale: false, canRecalculate: false };

function remind(documentId: number, latest: StaleReminderState): void {
  if (!latest.stale || !latest.canRecalculate) return;

  notifications.show({
    id: `stale-results-${String(documentId)}`,
    color: 'statusWarning',
    message: t('document.staleResults.leaveReminder', { id: documentId }),
    closeButtonProps: closeNotificationButtonProps,
  });
}

/**
 * Власник нагадування на рівні сторінки: показує тост лише при виході з документа (розмонтування сторінки
 * або інший `documentId`). Повертає сховище, куди рядок дій звітує поточний стан.
 */
export function useStaleResultsReminderHost(documentId: number): RefObject<StaleReminderState> {
  const host = useRef<StaleReminderState>(NothingToRemind);

  useEffect(
    () => () => {
      remind(documentId, host.current);
      // Стан належить ЦЬОМУ документові: наступний не успадковує чужого «застаріло».
      host.current = NothingToRemind;
    },
    [documentId],
  );

  return host;
}

/**
 * @param host Сховище власника-сторінки (`useStaleResultsReminderHost`): тоді хук лише звітує стан, а
 *   нагадує власник. Без нього хук нагадує сам - на власному розмонтуванні.
 */
export function useStaleResultsReminder(
  documentId: number,
  stale: boolean,
  canRecalculate: boolean,
  host?: RefObject<StaleReminderState>,
): void {
  const own = useRef<StaleReminderState>({ stale, canRecalculate });
  const latest = host ?? own;

  // ⚠ Без списку залежностей: власник скидає стан при зміні документа, і рядок мусить звітувати знову навіть
  // тоді, коли його значення не змінилися.
  useEffect(() => {
    latest.current = { stale, canRecalculate };
  });

  useEffect(
    () => () => {
      if (host === undefined) remind(documentId, own.current);
    },
    [documentId, host],
  );
}
