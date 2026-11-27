import { t } from '@/shared/i18n';

/**
 * ✎ DOCUX (огляд UI 27.11): режими версії методології словами. Перелік методологій показував сирі значення перелічень
 * («LEGACY · ERRORSONLY» — Badge ще й переводив їх у верхній регістр); тепер — «Legacy numbers · trace: errors only» і
 * пояснення кожного в підказці. Невідоме значення (новий режим сервера) — як є, не порожнеча.
 *
 * ⚠ Ключі — ЛІТЕРАЛАМИ (сторож каталогу розбирає лише так).
 */
export function numericModeLabel(mode: string): string {
  switch (mode) {
    case 'Legacy':
      return t('methodologies.mode.numeric.Legacy');
    case 'Strict':
      return t('methodologies.mode.numeric.Strict');
    default:
      return mode;
  }
}

export function numericModeHint(mode: string): string | null {
  switch (mode) {
    case 'Legacy':
      return t('methodologies.mode.numeric.LegacyHint');
    case 'Strict':
      return t('methodologies.mode.numeric.StrictHint');
    default:
      return null;
  }
}

export function traceLevelLabel(level: string): string {
  switch (level) {
    case 'Off':
      return t('methodologies.mode.trace.Off');
    case 'ErrorsOnly':
      return t('methodologies.mode.trace.ErrorsOnly');
    case 'Full':
      return t('methodologies.mode.trace.Full');
    default:
      return level;
  }
}

export function traceLevelHint(level: string): string | null {
  switch (level) {
    case 'Off':
      return t('methodologies.mode.trace.OffHint');
    case 'ErrorsOnly':
      return t('methodologies.mode.trace.ErrorsOnlyHint');
    case 'Full':
      return t('methodologies.mode.trace.FullHint');
    default:
      return null;
  }
}

export function calendarModeLabel(mode: string): string {
  switch (mode) {
    case 'Actual':
      return t('methodologies.mode.calendar.Actual');
    case 'Fixed365':
      return t('methodologies.mode.calendar.Fixed365');
    case 'Fixed360':
      return t('methodologies.mode.calendar.Fixed360');
    default:
      return mode;
  }
}

/** «Legacy numbers · trace: errors only» і пояснення обох (підказка); `calendar` — лише там, де його показують. */
export function versionModes(version: { readonly numericMode: string; readonly traceLevel: string; readonly calendarMode?: string }): {
  readonly label: string;
  readonly hint: string;
} {
  const parts = [numericModeLabel(version.numericMode)];
  if (version.calendarMode !== undefined) parts.push(calendarModeLabel(version.calendarMode));
  parts.push(traceLevelLabel(version.traceLevel));
  const hints = [numericModeHint(version.numericMode), traceLevelHint(version.traceLevel)].filter((hint): hint is string => hint !== null);

  return { label: parts.join(' · '), hint: hints.join(' ') };
}

/** Назва методології в переліку: «Назва (CODE)», а коли назва — сам код (чи порожня), код один раз. */
export function methodologyTitle(name: string, code: string): { readonly name: string; readonly code: string | null } {
  const trimmed = name.trim();
  return trimmed.length === 0 || trimmed === code ? { name: code, code: null } : { name: trimmed, code };
}

/** Чи підходить методологія під пошук (код або назва, без огляду на регістр). */
export function methodologyMatches(query: string, name: string, code: string): boolean {
  const needle = query.trim().toLocaleLowerCase();
  return needle.length === 0 || code.toLocaleLowerCase().includes(needle) || name.toLocaleLowerCase().includes(needle);
}
