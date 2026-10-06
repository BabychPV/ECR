import { lazy, Suspense, useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import type { RegistryEntryDto } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * ⚠ Діалог — окремий чанк (`import()`): на сторінку довідників він не потрібен, доки ніхто не
 * натиснув кнопку, і не має важити в жодному початковому бандлі.
 */
const EntryUsageModal = lazy(() => import('./EntryUsageModal'));

/**
 * Кнопка «Де використовується» в рядку запису (ФВ-8.14) разом зі своїм діалогом.
 *
 * ⚠ Стан відкриття живе тут, а не на сторінці: сторінці досить додати колонку з цією кнопкою.
 */
export function EntryUsageButton({
  registryCode,
  entry,
  siblings,
}: {
  registryCode: string;
  entry: RegistryEntryDto;
  siblings: readonly RegistryEntryDto[];
}): JSX.Element {
  const [opened, setOpened] = useState(false);

  return (
    <>
      <Button
        size="xs"
        variant="subtle"
        aria-label={t('registries.entryUsage.actionFor', { code: entry.code })}
        onClick={() => setOpened(true)}
      >
        {t('registries.entryUsage.action')}
      </Button>

      {opened && (
        <Suspense fallback={null}>
          <EntryUsageModal
            registryCode={registryCode}
            entry={entry}
            siblings={siblings}
            onClose={() => setOpened(false)}
          />
        </Suspense>
      )}
    </>
  );
}
