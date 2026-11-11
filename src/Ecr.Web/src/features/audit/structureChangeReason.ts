import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { siteUntilText } from '@/shared/siteMoment';

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

/**
 * ✎ debt-1109 F13 (P3 A): пояс майданчика з конверта причини (`p.zone`, журнал «Reopen») — у ньому показується й
 * стовпець часу запису, щоб «коли» і «до …» стояли в одному часі. Немає конверта чи поля — `null` (пояс читача).
 */
export function structureChangeReasonZone(raw: string | null | undefined): string | null {
  if (raw === null || raw === undefined || !raw.startsWith('{')) return null;

  try {
    const parsed = JSON.parse(raw) as { p?: { zone?: unknown } | null };
    const zone = parsed.p?.zone;
    return typeof zone === 'string' && zone.length > 0 ? zone : null;
  } catch {
    return null;
  }
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
    // D-464: масове видалення зниклих у PI подій підтверджено вручну (`SyncSourceEventsHandler`).
    case 'integrationAudit.sourceEventRemovalConfirmed':
      return t('integrationAudit.sourceEventRemovalConfirmed', params);
    case 'integrationAudit.registryPolicyChanged':
      return t('integrationAudit.registryPolicyChanged', params);
    case 'integrationAudit.dataSourceCreated':
      return t('integrationAudit.dataSourceCreated', params);
    case 'integrationAudit.dataSourceChanged':
      return t('integrationAudit.dataSourceChanged', params);
    // FP-7: зміна вікна дії запису довідника — скільки рядків перераховано (`SetEntryValidityHandler`).
    case 'registryAudit.entryValidityRescanned':
      return t('registryAudit.entryValidityRescanned', params);
    // F13-p213 (i5): місяць для одного документа — строк ISO.
    // ✎ debt-1109 i4-P3: у часі МАЙДАНЧИКА (`zone` із конверта), як рядок вкладки Periods; старі записи без `zone` —
    // у поясі читача, як і раніше.
    case 'document.reopen.audit.reopened': {
      const until = params['until'];
      const zone = params['zone'];
      const { zone: _zone, ...rest } = params;
      return t('document.reopen.audit.reopened', {
        ...rest,
        until:
          until === undefined
            ? ''
            : (zone === undefined || zone.length === 0 ? null : siteUntilText(until, zone)) ?? formatDateTime(until),
      });
    }
    case 'document.reopen.audit.closedExpired':
      return t('document.reopen.audit.closedExpired', params);
    case 'document.reopen.audit.closedReplaced':
      return t('document.reopen.audit.closedReplaced', params);
    // i4: «Close now» на вкладці Periods — людина закрила виняток до строку.
    case 'document.reopen.audit.closedManually':
      return t('document.reopen.audit.closedManually', params);
    default:
      return null;
  }
}
