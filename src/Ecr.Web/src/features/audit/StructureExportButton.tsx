import { useState, type JSX } from 'react';
import { Button, Group, Stack } from '@mantine/core';
import { fetchStructureExport } from '@/features/audit/api';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';

/** Ім'я файлу, коли сервер не надіслав `Content-Disposition`. */
export const FallbackExportFileName = 'audit-structure.csv';

/**
 * Вікно придатне для запиту: обидві дати задані й початок не пізніше кінця.
 *
 * ⚠ Ширину вікна (92 дні) клієнт свідомо НЕ перевіряє: межу знає сервер, і
 * його відмова `auditWindowTooWide` показується банером з його ж текстом.
 */
export function isExportWindowValid(from: string, to: string): boolean {
  return from.length > 0 && to.length > 0 && from <= to;
}

/**
 * «Експорт CSV» журналу структурних змін (`BE-16`).
 *
 * ⛔ Фільтри — ті самі, що бачить людина: вікно приходить від сторінки,
 * `entityType`/`changedBy` читаються з адреси тими самими ключами, що й у
 * `StructureChangesPanel`. Власна копія фільтрів розійшлася б із таблицею, і
 * файл містив би не те, що на екрані.
 *
 * ⛔ Без `Security.ViewAudit` кнопки НЕМАЄ — сервер відповів би `403`.
 */
export function StructureExportButton({ from, to }: { from: string; to: string }): JSX.Element | null {
  const session = useSession();
  const [entityType] = useUrlState('entityType');
  const [changedBy] = useUrlNumber('changedBy');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  if (!can(session.data, 'Security.ViewAudit')) return null;

  const run = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setError(null);

    try {
      const file = await fetchStructureExport({ from, to, entityType, changedByUserId: changedBy });
      saveBlob(file.blob, file.fileName ?? FallbackExportFileName);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap="xs" mb="md">
      <Group justify="flex-end">
        <Button
          size="xs"
          variant="default"
          loading={busy}
          disabled={!isExportWindowValid(from, to)}
          onClick={() => void run()}
        >
          {t('audit.exportCsv')}
        </Button>
      </Group>
      {error !== null && <ErrorAlert error={error} />}
    </Stack>
  );
}

/**
 * Віддає blob браузеру як завантаження.
 *
 * ⚠ URL звільняється одразу після кліку: інакше кожен експорт тримав би весь
 * файл у пам'яті вкладки до її закриття.
 */
function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
