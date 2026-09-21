import type { JSX } from 'react';
import { Code } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Людська назва поля в `MethodologyDiffItemDto.changedFields` (`BE-25`).
 *
 * ⚠ Перелік — рівно `MethodologyDiffFields.All` на сервері
 * (`Ecr.Application/Calculations/MethodologyDiffFields.cs`). Що кожне поле має
 * тут гілку, а в сіді — рядок, стереже `MethodologyDiffFieldCatalogTests`:
 * нове поле на сервері без назви — червоне з його ім'ям.
 *
 * ⚠ Ключі — літерали в кожній гілці, а не шаблон із `field`: так їх бачить
 * сторож каталогу (`EndpointCoverageTests`) і вимагає рядок сіду.
 *
 * ⛔ Невідоме значення (нове поле на сервері раніше за клієнт) — сире значення
 * в `Code`, а не порожньо й не вигадана назва: людина має бачити, ЩО саме
 * змінилося, навіть якщо назви ще немає.
 */
export function DiffFieldLabel({ field }: { field: string }): JSX.Element {
  switch (field) {
    case 'expression':
      return <>{t('methodologyDiffField.expression')}</>;
    case 'resultType':
      return <>{t('methodologyDiffField.resultType')}</>;
    case 'outputUnitId':
      return <>{t('methodologyDiffField.outputUnitId')}</>;
    case 'argumentsCsv':
      return <>{t('methodologyDiffField.argumentsCsv')}</>;
    case 'kind':
      return <>{t('methodologyDiffField.kind')}</>;
    case 'value':
      return <>{t('methodologyDiffField.value')}</>;
    case 'textValue':
      return <>{t('methodologyDiffField.textValue')}</>;
    case 'unitId':
      return <>{t('methodologyDiffField.unitId')}</>;
    case 'validTo':
      return <>{t('methodologyDiffField.validTo')}</>;
    case 'source':
      return <>{t('methodologyDiffField.source')}</>;
    case 'inputJson':
      return <>{t('methodologyDiffField.inputJson')}</>;
    case 'expectedJson':
      return <>{t('methodologyDiffField.expectedJson')}</>;
    case 'tolerance':
      return <>{t('methodologyDiffField.tolerance')}</>;
    default:
      return <Code>{field}</Code>;
  }
}
