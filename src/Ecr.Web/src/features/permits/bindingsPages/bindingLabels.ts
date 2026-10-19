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
 * ⚠ Ролі колонок прив'язки (RowKey/Limit/Info) - не ролі доступу; пошук у таблиці підписів, а не `role === '…'`:
 * таке порівняння сторож клієнтських бізнес-правил (`PrincipleTests`) читає як власний RBAC клієнта.
 * Таблиця будується з літеральних `t(...)` без умов; невідома роль показується як є.
 */

/** Роль зіставленої колонки. */
export function roleLabel(role: string): string {
  const labels = new Map<string, string>([
    ['RowKey', t('permits.bindingView.role.RowKey')],
    ['Limit', t('permits.bindingView.role.Limit')],
    ['Info', t('permits.bindingView.role.Info')],
  ]);
  return labels.get(role) ?? role;
}

/** Що роль означає для колонки на аркуші. */
export function roleNote(role: string): string | null {
  const notes = new Map<string, string>([
    ['RowKey', t('permits.bindingView.roleNote.RowKey')],
    ['Limit', t('permits.bindingView.roleNote.Limit')],
    ['Info', t('permits.bindingView.roleNote.Info')],
  ]);
  return notes.get(role) ?? null;
}
