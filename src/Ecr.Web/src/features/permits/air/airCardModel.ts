import { t } from '@/shared/i18n';
import type { AirSource, AirSubstance } from './airTypes';

/*
 * Модель картки «Air limits» (NAE-план N3): підпис типу джерела, прочерк для порожнього, пошук. Чиста, без React.
 */

/** Прочерк для порожнього значення (десяткові приходять рядками й показуються як є — без округлення, `t/year` повний). */
export const Dash = '—';

export function cell(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' ? Dash : value;
}

/**
 * Підпис типу джерела. Сервер віддає канонічні латинські `T` (точкове) і `P1` (площинне); кириличні `Т`/`П1` з
 * книги нормалізовані на сервері. Невідомий код показується як є — не вгадується.
 */
export function sourceTypeLabel(type: string | null): string {
  switch (type) {
    case 'T':
      return t('permits.air.type.point');
    case 'P1':
      return t('permits.air.type.diffuse');
    case null:
    case '':
      return Dash;
    default:
      return type;
  }
}

/** Скорочений код типу для вузької колонки (`Т` / `П1` кирилицею, як у книзі). */
export function sourceTypeShort(type: string | null): string {
  switch (type) {
    case 'T':
      return t('permits.air.type.pointShort');
    case 'P1':
      return t('permits.air.type.diffuseShort');
    default:
      return cell(type);
  }
}

function folded(value: string | null | undefined): string {
  return (value ?? '').toLocaleLowerCase();
}

function substanceMatches(substance: AirSubstance, needle: string): boolean {
  return folded(substance.substanceCode).includes(needle) || folded(substance.substanceName).includes(needle);
}

/** Чи відповідає джерело рядку пошуку: код НАЕ, назва, точка викиду, майданчик чи код/назва речовини. */
export function sourceMatches(source: AirSource, query: string): boolean {
  const needle = folded(query.trim());
  if (needle === '') return true;

  return (
    folded(source.naeCode).includes(needle) ||
    folded(source.sourceName).includes(needle) ||
    folded(source.emissionPointName).includes(needle) ||
    folded(source.siteLabel).includes(needle) ||
    source.substances.some((substance) => substanceMatches(substance, needle))
  );
}

export function filterSources(sources: readonly AirSource[], query: string): readonly AirSource[] {
  return query.trim() === '' ? sources : sources.filter((source) => sourceMatches(source, query));
}

/** Ключ рядка джерела: id рядка таблиці (унікальний), а не код НАЕ — один код повторюється під різними вузлами. */
export function sourceKey(source: AirSource): string {
  return String(source.id);
}
