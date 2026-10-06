import { useEffect, useRef, useState, useSyncExternalStore, type JSX } from 'react';
import { Anchor, Group, Text } from '@mantine/core';
import { hasSendablePending, subscribePending, usePendingCount } from '@/features/grid/pendingStore';
import { revealFirstHeldEdit } from '@/features/grid/settleEdits';
import { formatTime } from '@/shared/format/datetime';
import { formatCount } from '@/shared/format/plural';
import { t } from '@/shared/i18n';

/** Що відомо про документ для рядка стану збереження. */
interface DocumentSaveStateProps {
  /** Аркуш не редагується (стан, період, архів): «Read-only» замість «Saved». */
  readonly readOnly: boolean;
}

/**
 * Стан збереження документа СЛОВАМИ поруч із діями (UI-14; макет
 * `docs/design/hybrid/screen-document.js`, `renderSave`: «Saved 14:03»,
 * «3 unsaved changes», «Read-only»).
 *
 * ⚠ Джерело — сховище незбережених правок ДОКУМЕНТА (`pendingStore.ts`), а не
 * індикатор окремої сітки: правки живуть до збереження незалежно від того,
 * яка сітка змонтована (`D14-12`), і лічильник має бути той самий на будь-якому
 * аркуші.
 *
 * ⛔ Що тут НЕ вигадано:
 * - час «Saved HH:MM» — лише той, коли незбережене справді спорожніло в цій
 *   вкладці; до першого збереження — «All changes saved» без часу (часу
 *   останнього збереження до відкриття сторінки клієнт не знає);
 * - «не збережено» — лише коли правки тримає відмова сервера
 *   (`hasSendablePending() === false`): дебаунс автозбереження — це не збій.
 *   Повтор лишається кнопкою «Retry save» біля таблиці (`DocumentGrid.tsx`),
 *   тут — посилання «Show», що веде до першої відхиленої комірки.
 *
 * ⚠ `role="status"` + `aria-live="polite"`: читалка скаже зміну в паузі, не
 * перебиваючи введення.
 */
export function DocumentSaveState({ readOnly }: DocumentSaveStateProps): JSX.Element | null {
  const count = usePendingCount();
  const sendable = useSyncExternalStore(subscribePending, hasSendablePending, () => false);
  const [savedAt, setSavedAt] = useState<Date | null>(null);
  const previous = useRef(count);

  useEffect(() => {
    if (previous.current > 0 && count === 0) setSavedAt(new Date());
    previous.current = count;
  }, [count]);

  const held = count > 0 && !sendable;

  let text: string;
  let state: 'readOnly' | 'saved' | 'unsaved' | 'held';

  if (count > 0) {
    text = formatCount(count, 'document.saveState.unsaved');
    state = held ? 'held' : 'unsaved';
  } else if (readOnly) {
    text = t('document.saveState.readOnly');
    state = 'readOnly';
  } else {
    text = savedAt === null ? t('document.saveState.allSaved') : t('document.saveState.savedAt', { time: formatTime(savedAt) });
    state = 'saved';
  }

  return (
    <Group gap="xs" wrap="nowrap" role="status" aria-live="polite" data-testid="document-save-state" data-save-state={state}>
      <Text size="xs" c={held ? 'statusError' : 'dimmed'} fw={held ? 500 : undefined} style={{ whiteSpace: 'nowrap' }}>
        {text}
        {held && ` · ${t('document.saveState.notSaved')}`}
      </Text>
      {held && (
        <Anchor component="button" type="button" size="xs" onClick={() => revealFirstHeldEdit()}>
          {t('document.saveState.show')}
        </Anchor>
      )}
    </Group>
  );
}
