import { useState, type JSX } from 'react';
import { Button, Group, Stack } from '@mantine/core';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';

/** Завантажений CSV: тіло й ім'я, яке запропонував сервер (або `null`). */
export interface AuditExportFile {
  readonly blob: Blob;
  readonly fileName: string | null;
}

/**
 * Вікно придатне для запиту: обидві дати задані й початок не пізніше кінця.
 *
 * ⚠ Ширину вікна (92 дні) клієнт свідомо НЕ перевіряє: межу знає сервер, і його відмова `auditWindowTooWide`
 * показується банером з його ж текстом.
 */
function isExportWindowValid(from: string, to: string): boolean {
  return from.length > 0 && to.length > 0 && from <= to;
}

/**
 * «Експорт CSV» будь-якої вкладки журналу аудиту (комірки, структура, безпека, погодження).
 *
 * ⛔ Фільтри й порядок — ті самі, що бачить людина: `load` будує запит з тих самих джерел (адреса сторінки), що й
 * таблиця; власна копія розійшлася б, і файл містив би не те, що на екрані. Експорт — серверний потік (не
 * вивантаження сторінок клієнтом): `fetch` → blob, бо за посиланням браузер показав би відмову (`422`, `403`)
 * сирим JSON; тут вона стає `EcrApiError` і показується `ErrorAlert`-ом із текстом сервера.
 *
 * ⛔ Без `Security.ViewAudit` кнопки НЕМАЄ — сервер відповів би `403`.
 */
export function AuditExportButton({
  from,
  to,
  fallbackName,
  load,
  tab,
}: {
  readonly from: string;
  readonly to: string;
  readonly fallbackName: string;
  readonly load: () => Promise<AuditExportFile>;
  /** Позначка вкладки для тестів/e2e (`data-audit-export`). */
  readonly tab: string;
}): JSX.Element | null {
  const session = useSession();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  if (!can(session.data, 'Security.ViewAudit')) return null;

  const run = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setError(null);

    try {
      const file = await load();
      saveBlob(file.blob, file.fileName ?? fallbackName);
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
          variant="default"
          loading={busy}
          disabled={!isExportWindowValid(from, to)}
          data-audit-export={tab}
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
 * ⚠ URL звільняється одразу після кліку: інакше кожен експорт тримав би весь файл у пам'яті вкладки до її закриття.
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
