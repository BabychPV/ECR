import { useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ImportApplyRequest, ImportPreview } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Куди імпортувати. */
export interface ImportPanelProps {
  /** Документ. */
  documentId: number;
  /** Період — після застосування перечитуються саме його зрізи. */
  periodKey: number;
}

/**
 * Імпорт із **обов'язковим переглядом diff** (`ФВ-4.3`, модуль 6.10).
 *
 * ⛔ Перегляд — не крок майстра, який можна пропустити, а сам механізм
 * захисту. Аркуш Excel, застосований без перегляду, непомітно перезаписує
 * чужу роботу: оператор бачить у себе свої числа, а в базі лежать чужі, і
 * дізнається про це через місяць зі звірки. Тому тут два виклики, а не один,
 * і другий приймає **токен першого**: застосувати можна лише те, що показали.
 *
 * ⚠ Обидві дії не мали в інтерфейсі жодного споживача до аудиту (`A7-39`) —
 * тобто весь модуль імпорту існував на сервері й був недосяжний.
 */
export function ImportPanel({ documentId, periodKey }: ImportPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const picker = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);

  const load = useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();

      // ⚠ Ім'я поля — `file`: саме так називається параметр `IFormFile file`
      // у контролері. Будь-яке інше дає 400 з порожнім тілом, і причина
      // виглядає як «файл не підійшов».
      form.append('file', file);

      return apiFetch<ImportPreview>(`/api/v1/documents/${documentId}/import/preview`, {
        method: 'POST',
        body: form,
      });
    },
    onSuccess: setPreview,
    onError: showApiError,
  });

  const apply = useMutation({
    mutationFn: (previewToken: string) =>
      apiFetch(`/api/v1/documents/${documentId}/import/apply`, {
        method: 'POST',
        body: JSON.stringify({ previewToken } satisfies ImportApplyRequest),
      }),
    onSuccess: async () => {
      // Зрізи таблиць перечитуються цілком: імпорт зачіпає рядки, яких немає
      // на екрані, і часткове оновлення показало б половину змін.
      await queryClient.invalidateQueries({ queryKey: ['table-slice'] });
      await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });

      setPreview(null);
      showDone(t('import.applied'));
    },
    // ⚠ Конфлікт версій рядків (`ECR-CELL-0409`) означає, що між переглядом і
    // застосуванням хтось змінив ті самі комірки. Батч відхиляється цілком —
    // часткове застосування заборонене (`B04` §2.3).
    onError: showApiError,
  });

  const blocked = (preview?.conflicts.length ?? 0) > 0 || (preview?.rejected.length ?? 0) > 0;

  return (
    <>
      {/* ⚠ Прихований `input[type=file]` за кнопкою: рідний елемент не
          піддається оформленню, але саме він дає діалог вибору файлу і
          працює з клавіатури. Кнопка лише натискає його. */}
      <input
        ref={picker}
        type="file"
        accept=".xlsx"
        hidden
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const file = event.currentTarget.files?.[0];
          if (file !== undefined) load.mutate(file);

          // Скидання дозволяє обрати ТОЙ САМИЙ файл удруге: без нього
          // повторний вибір не викликає `change`, і кнопка «не працює».
          event.currentTarget.value = '';
        }}
      />

      <Button
        size="xs"
        variant="default"
        loading={load.isPending}
        onClick={() => picker.current?.click()}
      >
        {t('import.pick')}
      </Button>

      <Modal
        opened={preview !== null}
        onClose={() => setPreview(null)}
        title={t('import.title')}
        size="xl"
      >
        {preview !== null && (
          <Stack gap="sm">
            <Group gap="xs">
              <Badge variant="light">{t('import.changes', { count: preview.changes.length })}</Badge>
              {preview.conflicts.length > 0 && (
                <Badge color="orange">
                  {t('import.conflicts', { count: preview.conflicts.length })}
                </Badge>
              )}
              {preview.rejected.length > 0 && (
                <Badge color="red">
                  {t('import.rejected', { count: preview.rejected.length })}
                </Badge>
              )}
            </Group>

            {blocked && (
              // ⛔ Застосування заблоковане цілком, а не «застосуємо решту».
              // Часткове застосування заборонене на рівні API, і імітувати
              // його тут означало б показати успіх там, де сервер відмовить.
              <Alert color="orange" title={t('import.blockedTitle')}>
                {t('import.blockedHint')}
              </Alert>
            )}

            {preview.changes.length === 0 && !blocked && (
              <Text size="sm">{t('import.noChanges')}</Text>
            )}

            {preview.changes.length > 0 && (
              <Table striped withTableBorder className="ecr-sticky-head">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('import.row')}</Table.Th>
                    <Table.Th>{t('import.column')}</Table.Th>
                    <Table.Th>{t('import.was')}</Table.Th>
                    <Table.Th>{t('import.becomes')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.changes.map((change) => (
                    <Table.Tr key={`${change.rowKey}:${change.columnCode}`}>
                      <Table.Td>{change.rowKey}</Table.Td>
                      <Table.Td>{change.columnCode}</Table.Td>
                      <Table.Td>{show(change.oldValue)}</Table.Td>
                      <Table.Td>{show(change.newValue)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {preview.rejected.length > 0 && (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('import.row')}</Table.Th>
                    <Table.Th>{t('import.column')}</Table.Th>
                    <Table.Th>{t('import.reason')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {preview.rejected.map((rejection) => (
                    <Table.Tr key={`${rejection.rowKey}:${rejection.columnCode}`}>
                      <Table.Td>{rejection.rowKey}</Table.Td>
                      <Table.Td>{rejection.columnCode}</Table.Td>
                      <Table.Td>
                        {rejection.message} ({rejection.reasonCode})
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={() => setPreview(null)}>
                {t('common.cancel')}
              </Button>
              <Button
                disabled={blocked || preview.changes.length === 0}
                loading={apply.isPending}
                onClick={() => apply.mutate(preview.previewToken)}
              >
                {t('import.apply')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </>
  );
}

/**
 * Значення комірки в таблиці diff.
 *
 * ⚠ Порожнеча показується прочерком, а не порожнім місцем: «було порожньо —
 * стане 12» і «було 12 — стане порожньо» мають виглядати як зміни, а не як
 * половина рядка.
 */
function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';

  return String(value);
}
