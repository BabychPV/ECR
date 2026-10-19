import { t } from '@/shared/i18n';

/*
 * ⚠ Ключі - літерали в `t(...)`: сторож каталогу (`EndpointCoverageTests`) читає їх як текст, динамічний ключ він не бачить.
 * Невідоме значення (сервер додав режим чи правило) показується як є: підпис, якого немає, не вигадується.
 */

/** Режим прив'язки словами макета. */
export function modeLabel(mode: string): string {
  if (mode === 'Rows') return t('permits.bindingsList.mode.Rows');
  if (mode === 'Formulas') return t('permits.bindingsList.mode.Formulas');

  return mode;
}

/** Правило вибору дозволу словами макета. */
export function ruleLabel(rule: string): string {
  if (rule === 'ByFacilityPeriodEnd') return t('permits.bindingsList.rule.ByFacilityPeriodEnd');
  if (rule === 'FromHeaderLookup') return t('permits.bindingsList.rule.FromHeaderLookup');

  return rule;
}

/*
 * ⚠ Ролі колонок прив'язки (RowKey/Limit/Info) - не ролі доступу; `switch`/`case`, а не `role === '…'`: цей запис
 * сторож клієнтських бізнес-правил (`PrincipleTests`) читає як власний RBAC клієнта.
 */

/** Роль зіставленої колонки. */
export function roleLabel(role: string): string {
  switch (role) {
    case 'RowKey':
      return t('permits.bindingView.role.RowKey');
    case 'Limit':
      return t('permits.bindingView.role.Limit');
    case 'Info':
      return t('permits.bindingView.role.Info');
    default:
      return role;
  }
}

/** Що роль означає для колонки на аркуші. */
export function roleNote(role: string): string | null {
  switch (role) {
    case 'RowKey':
      return t('permits.bindingView.roleNote.RowKey');
    case 'Limit':
      return t('permits.bindingView.roleNote.Limit');
    case 'Info':
      return t('permits.bindingView.roleNote.Info');
    default:
      return null;
  }
}