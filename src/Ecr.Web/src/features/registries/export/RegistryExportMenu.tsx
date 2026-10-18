import { useState, type JSX } from 'react';
import { Alert, Button, Checkbox, Modal, Popover, Stack, Text } from '@mantine/core';
import { fetchRegistryExport, type RegistryExportFormat } from '@/features/registries/rows/api';
import { t } from '@/shared/i18n';
import { registryExportErrorText } from './exportError';

/**
 * Чи сервер уже вміє `?includeChildren=true` (частини композиції, ФВ-8.16).
 *
 * ⚠ `true`: серверний `GET /registries/{code}/export?includeChildren=true` у вершині
 * `dev/integration` (`65f70ec0`, контракт `98ae5eb8`). CSV із частинами — ZIP (`01-БАТЬКО.csv`…),
 * XLSX — аркуш на довідник; ім'я файлу — з `Content-Disposition`, запасне для ZIP — `.zip`.
 */
export const IncludeChildrenAvailable = true;

export interface RegistryExportMenuProps {
  readonly registryCode: string;
  /** Бізнес-дата чинності `yyyy-MM-dd`; `null` — сервер візьме сьогодні (UTC). */
  readonly asOf: string | null;

  /**
   * Керований режим (REG-UI 3/3): `opened` задано — замість кнопки з випадним вікном діалог, який
   * відкриває й закриває сторінка (пункт меню «Ще»). Без нього — як було.
   */
  readonly opened?: boolean;
  readonly onClose?: () => void;
}

/**
 * «Експорт» довідника (RT-16): CSV (приймає назад імпорт) або XLSX, записи чинні на `asOf`.
 *
 * ⚠ `export default` — вимога `React.lazy()`: меню вантажиться окремим чанком, у сторінку
 * потрапляє лише обгортка.
 *
 * ⛔ `fetch` → blob → завантаження, а не посилання: відмову (`403`, `422` стелі) людина бачить
 * реченням тут-таки, а не сирим JSON у новій вкладці.
 */
export default function RegistryExportMenu({
  registryCode,
  asOf,
  opened: controlledOpened,
  onClose,
}: RegistryExportMenuProps): JSX.Element {
  const controlled = controlledOpened !== undefined;
  const [ownOpened, setOwnOpened] = useState(false);
  const opened = controlled ? controlledOpened : ownOpened;
  const setOpened = (next: boolean): void => {
    setOwnOpened(next);
    if (!next) onClose?.();
  };
  const [busy, setBusy] = useState<RegistryExportFormat | null>(null);
  const [includeChildren, setIncludeChildren] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const run = async (format: RegistryExportFormat): Promise<void> => {
    if (busy !== null) return;
    setBusy(format);
    setError(null);

    try {
      const withChildren = IncludeChildrenAvailable && includeChildren;
      const file = await fetchRegistryExport(registryCode, format, asOf ?? undefined, withChildren);
      const extension = withChildren && format === 'csv' ? 'zip' : format;
      saveBlob(file.blob, file.fileName ?? `registry-${registryCode}.${extension}`);
      setOpened(false);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  const shown = error === null ? null : registryExportErrorText(error);

  const body = (
    <Stack gap="xs">
      {asOf !== null && (
        <Text size="xs" c="dimmed">
          {t('registries.export.asOfHint', { date: asOf })}
        </Text>
      )}
      <Checkbox
        size="xs"
        label={t('registries.export.includeChildren')}
        description={IncludeChildrenAvailable ? undefined : t('registries.export.includeChildrenUnavailable')}
        disabled={!IncludeChildrenAvailable}
        checked={IncludeChildrenAvailable && includeChildren}
        onChange={(event) => setIncludeChildren(event.currentTarget.checked)}
      />
      <Button variant="default" loading={busy === 'csv'} disabled={busy !== null} onClick={() => void run('csv')}>
        {t('registries.export.csv')}
      </Button>
      <Button variant="default" loading={busy === 'xlsx'} disabled={busy !== null} onClick={() => void run('xlsx')}>
        {t('registries.export.xlsx')}
      </Button>
      {shown !== null && (
        <Alert color="statusError" title={shown.title} role="alert" data-testid="registry-export-error">
          <Text size="sm">{shown.detail}</Text>
          {shown.hint !== null && (
            <Text size="xs" c="dimmed" mt="xs">
              {shown.hint}
            </Text>
          )}
        </Alert>
      )}
    </Stack>
  );

  // Керований режим: діалог без власної кнопки (пункт меню «Ще» на сторінці).
  if (controlled) {
    return (
      <Modal opened={opened} onClose={() => setOpened(false)} title={t('registries.export.button')} size="sm">
        {body}
      </Modal>
    );
  }

  return (
    <Popover opened={opened} onChange={setOpened} position="bottom-end" width={320} withinPortal trapFocus>
      <Popover.Target>
        <Button variant="default" onClick={() => setOpened(!opened)} aria-expanded={opened}>
          {t('registries.export.button')}
        </Button>
      </Popover.Target>
      <Popover.Dropdown>{body}</Popover.Dropdown>
    </Popover>
  );
}

/**
 * Віддає blob браузеру як завантаження.
 *
 * ⚠ URL звільняється одразу після кліку: інакше кожен експорт тримав би весь файл у пам'яті
 * вкладки до її закриття.
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
