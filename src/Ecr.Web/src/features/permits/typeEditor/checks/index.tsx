import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { EmptyState } from '@/shared/ui/AsyncBoundary';

/**
 * Тіло вкладки «Checks». ⚠ Місце для PM-08c: цей файл замінюється справжньою таблицею перевірок
 * (мокап: `rulesTab`). Доти — чесний порожній стан, без обіцянок: перевірок тут ще не показано.
 */
export function TypeChecksTab(): JSX.Element {
  return <EmptyState title={t('permits.editor.checks.emptyTitle')} hint={t('permits.editor.checks.emptyHint')} />;
}
