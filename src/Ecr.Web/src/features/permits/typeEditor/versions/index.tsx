import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { EmptyState } from '@/shared/ui/AsyncBoundary';

/**
 * Тіло вкладки «Versions». ⚠ Місце для PM-08c: цей файл замінюється справжньою таблицею версій
 * (мокап: `versions`). Доти — чесний порожній стан, без обіцянок: версій тут ще не показано.
 */
export function TypeVersionsTab(): JSX.Element {
  return <EmptyState title={t('permits.editor.versions.emptyTitle')} hint={t('permits.editor.versions.emptyHint')} />;
}
