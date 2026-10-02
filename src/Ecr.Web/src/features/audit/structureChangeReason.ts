import { t } from '@/shared/i18n';

/**
 * Причина структурної зміни (`StructureChange.ChangeReason`) мовою інтерфейсу.
 *
 * ⛔ Зміни налаштувань збору (`IntegrationConfigAudit.Reason`) пишуть причину КОНВЕРТОМ
 * `{"k":"integrationAudit.…","p":{…}}`: у момент запису мова читача невідома, а готова
 * українська фраза на англійському інтерфейсі — P3 живого проходу екрана джерел.
 *
 * ⚠ Рядок, що не є конвертом (старі записи, причина, введена людиною), і конверт із
 * невідомим ключем повертаються як є: сирий рядок чесніший за вгаданий переклад.
 * Ключі — ЛІТЕРАЛАМИ, щоб сторож `EndpointCoverageTests` звіряв кожен із `09-seed.sql`.
 */
export function structureChangeReasonText(raw: string): string {
  if (!raw.startsWith('{')) return raw;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return raw;
  }

  if (typeof parsed !== 'object' || parsed === null) return raw;

  const { k, p } = parsed as { k?: unknown; p?: unknown };
  if (typeof k !== 'string') return raw;
  if (p !== undefined && (typeof p !== 'object' || p === null)) return raw;

  return render(k, { ...(p as Record<string, string> | undefined) }) ?? raw;
}

function render(key: string, params: Record<string, string>): string | null {
  switch (key) {
    case 'integrationAudit.scheduleCreated':
      return t('integrationAudit.scheduleCreated', params);
    case 'integrationAudit.scheduleChanged':
      return t('integrationAudit.scheduleChanged', params);
    case 'integrationAudit.scheduleDependencyCleared':
      return t('integrationAudit.scheduleDependencyCleared', params);
    case 'integrationAudit.scheduleDeleted':
      return t('integrationAudit.scheduleDeleted', params);
    case 'integrationAudit.fieldMapCreated':
      return t('integrationAudit.fieldMapCreated', params);
    case 'integrationAudit.fieldMapPaused':
      return t('integrationAudit.fieldMapPaused', params);
    case 'integrationAudit.fieldMapResumed':
      return t('integrationAudit.fieldMapResumed', params);
    case 'integrationAudit.fieldMapUnitAccepted':
      return t('integrationAudit.fieldMapUnitAccepted', params);
    case 'integrationAudit.fieldMapDeleted':
      return t('integrationAudit.fieldMapDeleted', params);
    case 'integrationAudit.rowWindowCreated':
      return t('integrationAudit.rowWindowCreated', params);
    case 'integrationAudit.rowWindowChanged':
      return t('integrationAudit.rowWindowChanged', params);
    case 'integrationAudit.rowWindowDeleted':
      return t('integrationAudit.rowWindowDeleted', params);
    case 'integrationAudit.sourceEntityCreated':
      return t('integrationAudit.sourceEntityCreated', params);
    case 'integrationAudit.sourceEntityBound':
      return t('integrationAudit.sourceEntityBound', params);
    case 'integrationAudit.sourceEntityUnbound':
      return t('integrationAudit.sourceEntityUnbound', params);
    case 'integrationAudit.eventMapCreated':
      return t('integrationAudit.eventMapCreated', params);
    case 'integrationAudit.eventMapChanged':
      return t('integrationAudit.eventMapChanged', params);
    case 'integrationAudit.eventMapDeleted':
      return t('integrationAudit.eventMapDeleted', params);
    case 'integrationAudit.registryPolicyChanged':
      return t('integrationAudit.registryPolicyChanged', params);
    case 'integrationAudit.dataSourceCreated':
      return t('integrationAudit.dataSourceCreated', params);
    case 'integrationAudit.dataSourceChanged':
      return t('integrationAudit.dataSourceChanged', params);
    default:
      return null;
  }
}
