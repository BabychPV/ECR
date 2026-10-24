import { localized } from '@/shared/i18n/localized';
import type { DataSource } from './dataSourceApi';

/**
 * Назва з'єднання мовою користувача; немає жодної — код.
 *
 * ⚠ `nameL10n` у `DataSourceView` — ПЛОСКА мапа мов, а не `{ values }`, як
 * у решті `…L10n`; тому обгортка тут, а не зміна `localized`.
 *
 * ⚠ Окремим модулем (`D-132`): перелік з'єднань і журнал прогонів тягнуть лише назву, а не шухляду з'єднання
 * (`DataSourceDrawer` — лінивий чанк). Шухляда реекспортує — наявні імпорти й тести без змін.
 */
export function dataSourceName(source: DataSource): string {
  const name = localized({ values: source.nameL10n });

  return name.length > 0 ? name : source.code;
}
