import { Suspense, lazy, type JSX } from 'react';
import { Button } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { RegistryExportMenuProps } from './RegistryExportMenu';

/** Меню експорту — окремим чанком: у сторінку потрапляє лише ця обгортка. */
const RegistryExportMenu = lazy(() => import('./RegistryExportMenu'));

/**
 * Кнопка «Експорт» довідника (RT-16) для переліку довідників і редактора записів.
 *
 * ⚠ Поки чанк вантажиться — та сама кнопка, вимкнена: місце в рядку дій не стрибає.
 */
export function RegistryExportButton(props: RegistryExportMenuProps): JSX.Element {
  return (
    <Suspense
      fallback={
        <Button size="xs" variant="default" disabled>
          {t('registries.export.button')}
        </Button>
      }
    >
      <RegistryExportMenu {...props} />
    </Suspense>
  );
}
