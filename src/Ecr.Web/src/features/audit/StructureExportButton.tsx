import type { JSX } from 'react';
import { fetchStructureExport } from '@/features/audit/api';
import { useAuditSort } from '@/features/audit/auditSort';
import { AuditExportButton } from '@/features/audit/AuditExportButton';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';

/** Ім'я файлу, коли сервер не надіслав `Content-Disposition`. */
const FallbackExportFileName = 'audit-structure.csv';

/**
 * «Експорт CSV» журналу структурних змін (`BE-16`).
 *
 * ⛔ Фільтри й порядок — ті самі, що бачить людина: вікно приходить від сторінки, `entityType`/`changedBy`/`sort`
 * читаються з адреси тими самими ключами, що й у `StructureChangesPanel`.
 */
export function StructureExportButton({ from, to }: { from: string; to: string }): JSX.Element | null {
  const [entityType] = useUrlState('entityType');
  const [changedBy] = useUrlNumber('changedBy');
  const [sort] = useAuditSort();

  return (
    <AuditExportButton
      tab="structure"
      from={from}
      to={to}
      fallbackName={FallbackExportFileName}
      load={() => fetchStructureExport({ from, to, entityType, changedByUserId: changedBy, sort })}
    />
  );
}
