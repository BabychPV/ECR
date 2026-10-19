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

/** Роль зіставленої колонки. */
export function roleLabel(role: string): string {
  if (role === 'RowKey') return t('permits.bindingView.role.RowKey');
  if (role === 'Limit') return t('permits.bindingView.role.Limit');
  if (role === 'Info') return t('permits.bindingView.role.Info');

  return role;
}

/** Що роль означає для колонки на аркуші. */
export function roleNote(role: string): string | null {
  if (role === 'RowKey') return t('permits.bindingView.roleNote.RowKey');
  if (role === 'Limit') return t('permits.bindingView.roleNote.Limit');
  if (role === 'Info') return t('permits.bindingView.roleNote.Info');

  return null;
}
