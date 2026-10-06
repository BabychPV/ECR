import type { RoleView } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/**
 * Назва ролі мовою інтерфейсу з `GET /api/v1/roles` (`RoleView.nameL10n` —
 * те, що пише `CreateRoleModal`); без назви — код.
 *
 * ✎ Доти `RoleView` назви не віддавав, і роль показувалась лише кодом.
 */
export function roleLabel(role: RoleView | undefined): string {
  const name = localized(role?.nameL10n);

  return name.length > 0 ? name : (role?.code ?? '');
}
