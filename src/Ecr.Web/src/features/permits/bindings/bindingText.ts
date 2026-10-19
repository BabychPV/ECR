import { t } from '@/shared/i18n';
import type { NoPermitReason, WarningKind } from './bindingModel';

/*
 * Тексти причин і попереджень. Ключі каталогу — ЛИТЕРАЛАМИ в `t('…')`: сторож `EndpointCoverageTests` не
 * приймає `t(змінна)` (див. `DynamicKeySites`), тож відображення «випадок → ключ» живе тут, а не в моделі.
 */

export function reasonText(kind: NoPermitReason): string {
  switch (kind) {
    case 'noFacility':
      return t('permits.sheetBinding.preview.noFacility');
    case 'noPermit':
      return t('permits.sheetBinding.preview.noPermit');
    case 'headerEmpty':
      return t('permits.sheetBinding.preview.headerEmpty');
    case 'headerUnknown':
      return t('permits.sheetBinding.preview.headerUnknown');
    case 'other':
      return t('permits.sheetBinding.preview.noneOther');
  }
}

export function warningText(kind: WarningKind): string {
  switch (kind) {
    case 'inactive':
      return t('permits.sheetBinding.warning.inactive');
    case 'formulasMode':
      return t('permits.sheetBinding.warning.formulasMode');
    case 'notRowsBinding':
      return t('permits.sheetBinding.warning.notRowsBinding');
    case 'documentOtherVersion':
      return t('permits.sheetBinding.warning.documentOtherVersion');
    case 'tableUnavailable':
      return t('permits.sheetBinding.warning.tableUnavailable');
    case 'fieldUnreadable':
      return t('permits.sheetBinding.warning.fieldUnreadable');
    case 'notValidAtPeriodEnd':
      return t('permits.sheetBinding.warning.notValidAtPeriodEnd');
    case 'rowCountUnknown':
      return t('permits.sheetBinding.warning.rowCountUnknown');
    case 'rowsClamped':
      return t('permits.sheetBinding.warning.rowsClamped');
    case 'planFailed':
      return t('permits.sheetBinding.warning.planFailed');
    case 'valueSkipped':
      return t('permits.sheetBinding.warning.valueSkipped');
    case 'rowDeleted':
      return t('permits.sheetBinding.warning.rowDeleted');
    case 'cellNotFromPermit':
      return t('permits.sheetBinding.warning.cellNotFromPermit');
    case 'periodUnknown':
      return t('permits.sheetBinding.warning.periodUnknown');
    case 'other':
      return t('permits.sheetBinding.warning.other');
  }
}