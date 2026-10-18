import { DefaultLanguage, language, t } from '@/shared/i18n';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { ListScopes } from './draftModel';

/*
 * Підписи редактора типу (Permits, PM-08b). Назви в контракті — `{ en, ru, kz }`, не `LocalizedText`
 * каталогу, тож `localized()` тут не годиться.
 */

/** Текст мовою користувача → мовою за замовчуванням → першим наявним → порожньо. */
export function textOf(text: Readonly<Record<string, string>> | null | undefined, lang: string = language()): string {
  if (text === null || text === undefined) return '';
  const exact = text[lang];
  if (exact !== undefined && exact.trim() !== '') return exact;
  const fallback = text[DefaultLanguage];
  if (fallback !== undefined && fallback.trim() !== '') return fallback;
  return Object.values(text).find((value) => value.trim() !== '') ?? '';
}

/** Другий підпис під заголовком: інша мова, ніж основний (мокап: російська назва під англійською). */
export function secondaryTextOf(text: Readonly<Record<string, string>>, primary: string): string {
  const order = ['ru', 'en', 'kz'];
  for (const lang of order) {
    const value = text[lang];
    if (value !== undefined && value.trim() !== '' && value !== primary) return value;
  }
  return '';
}

/** Підпис типу даних стовпця для таблиці: «Link → Substances», «Choice», «Number». */
export function columnTypeLabel(column: PermitTypeColumn, tables: readonly PermitTypeTable[]): string {
  if (column.dataType === 'Lookup') {
    switch (column.listScope) {
      case ListScopes.shared:
        return t('permits.editor.type.linkRegistry', { name: column.listRegistryCode ?? '' });
      case ListScopes.type:
        return t('permits.editor.type.choice');
      case ListScopes.permit: {
        const sibling = tables.find((table) => table.code.toLowerCase() === (column.siblingTableCode ?? '').toLowerCase());
        return t('permits.editor.type.linkTable', { name: sibling === undefined ? (column.siblingTableCode ?? '') : textOf(sibling.nameL10n) });
      }
      default:
        return t('permits.editor.type.Lookup');
    }
  }
  return dataTypeLabel(column.dataType);
}

/** Підпис типу даних без області списку. Невідомий тип показується як є. */
export function dataTypeLabel(dataType: string): string {
  switch (dataType) {
    case 'String':
      return t('permits.editor.type.String');
    case 'Int':
      return t('permits.editor.type.Int');
    case 'Decimal':
      return t('permits.editor.type.Decimal');
    case 'Bool':
      return t('permits.editor.type.Bool');
    case 'Date':
      return t('permits.editor.type.Date');
    case 'Lookup':
      return t('permits.editor.type.Lookup');
    default:
      return dataType;
  }
}

/** Код реєстру таблиці — як його складає сервер (`PRM_<тип>_<таблиця>`). */
export function tableRegistryCode(typeCode: string, tableCode: string): string {
  return `PRM_${typeCode}_${tableCode}`;
}

/** Код реєстру шапки типу. */
export function headerRegistryCode(typeCode: string): string {
  return `PRM_${typeCode}`;
}
