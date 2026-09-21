import type { JSX } from 'react';
import { Code } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Людська назва виду залежного об'єкта (`UsageItemDto.kind`) — спільна для
 * обох «де використано»: довідника (`BE-24`) і одиниці (`BE-15`).
 *
 * ⚠ Перелік — рівно `UsageKinds.All` на сервері (`Ecr.Application/Common/UsageKinds.cs`).
 * Що кожен вид має тут гілку, а в сіді — рядок, стереже
 * `UsageKindCatalogTests`: новий вид на сервері без назви — червоне з його ім'ям.
 *
 * ⚠ Ключі — літерали в кожній гілці, а не шаблон із `kind`: так їх бачить
 * сторож каталогу (`EndpointCoverageTests`) і вимагає рядок сіду.
 *
 * ⛔ Невідоме значення (новий вид на сервері раніше за клієнт) — сире значення
 * в `Code`, а не порожньо й не вигадана назва: людина має бачити, ЩО саме
 * залежить від об'єкта, навіть якщо назви ще немає.
 */
export function UsageKindLabel({ kind }: { kind: string }): JSX.Element {
  switch (kind) {
    case 'templateColumn':
      return <>{t('usageKind.templateColumn')}</>;
    case 'registryField':
      return <>{t('usageKind.registryField')}</>;
    case 'methodologySubstance':
      return <>{t('usageKind.methodologySubstance')}</>;
    case 'sourceEntity':
      return <>{t('usageKind.sourceEntity')}</>;
    case 'methodologyConstant':
      return <>{t('usageKind.methodologyConstant')}</>;
    case 'methodologyFormula':
      return <>{t('usageKind.methodologyFormula')}</>;
    case 'methodologyOutput':
      return <>{t('usageKind.methodologyOutput')}</>;
    case 'fieldMap':
      return <>{t('usageKind.fieldMap')}</>;
    case 'unitConversion':
      return <>{t('usageKind.unitConversion')}</>;
    case 'derivedUnit':
      return <>{t('usageKind.derivedUnit')}</>;
    case 'dimensionBase':
      return <>{t('usageKind.dimensionBase')}</>;
    case 'data':
      return <>{t('usageKind.data')}</>;
    default:
      return <Code>{kind}</Code>;
  }
}
