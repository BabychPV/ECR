import { useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Stack, Table } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryEntryImportReport } from '@/api/types';
import { importRegistryEntries } from '@/features/registries/api';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { usePendingLoading } from '@/features/common/usePendingLoading';

/** Куди імпортувати. */
export interface RegistryImportPanelProps {
  /** Код довідника, у записи якого імпортують. */
  registryCode: string;

  /** D-211: довідник External — записи лише синком з AF, імпорт недоступний. */
  disabled?: boolean;
}

/**
 * Імпорт записів довідника з CSV (`BE-24`).
 *
 * ⚠ Патерн той самий, що в `features/import/ImportPanel.tsx` (вибір файлу →
 * обов'язковий перегляд → застосування), АЛЕ це НЕ preview/apply-токен.
 * Контракт — ОДИН ендпоінт із булевим `dryRun`: перший виклик (`dryRun=true`)
 * нічого не записує й повертає звіт для перегляду, другий (`dryRun=false`)
 * повторно надсилає ТОЙ САМИЙ файл — саме тому вибраний `File` лишається в
 * стані компонента між двома викликами, а не токен першої відповіді.
 *
 * ⛔ Усе-або-нічого: хоч одна помилка рядка — і сервер не записав нічого,
 * навіть якщо `dryRun=false`. Кнопка «Застосувати» недоступна, доки в звіті
 * перегляду є хоч одна помилка (`blocked` нижче) — так само, як `ImportPanel`
 * блокує застосування при конфліктах чи відхиленнях.
 */
export function RegistryImportPanel({ registryCode, disabled = false }: RegistryImportPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const picker = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<RegistryEntryImportReport | null>(null);

  // ⛔ L9-44: довідник, ДО якого перевірено файл. Панель живе далі, коли на
  // сторінці обирають інший довідник (`registryCode` міняється без перемонтування),
  // і без цієї прив'язки «Застосувати» слало файл, перевірений проти одного
  // довідника, у записи іншого. Звіт чужого довідника не показується.
  const [target, setTarget] = useState<string | null>(null);
  const reviewed = report !== null && target === registryCode ? report : null;

  const preview = useMutation({
    mutationFn: ({ code, selected }: { code: string; selected: File }) =>
      importRegistryEntries(code, selected, true),
    onSuccess: (result, { code, selected }) => {
      // ⚠ Файл запам'ятовується РАЗОМ зі звітом і довідником: «Застосувати»
      // надішле саме його і туди ж, а не попросить обрати ще раз.
      setTarget(code);
      setFile(selected);
      setReport(result);
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const previewLoading = usePendingLoading(preview.isPending);

  const apply = useMutation({
    mutationFn: () => {
      if (file === null || target !== registryCode) {
        // Недосяжно з інтерфейсу: кнопка нижче лише в діалозі перевіреного файлу.
        return Promise.reject(new Error('registry import: apply without a file'));
      }

      // ⛔ ТОЙ САМИЙ файл, вдруге: контракт не несе токена першого виклику —
      // намір підтверджує повторне читання файлу з `dryRun=false`, а не
      // пред'явлення довіреності.
      return importRegistryEntries(target, file, false);
    },
    onSuccess: async (result) => {
      setReport(result);
      const code = target ?? registryCode;

      // ⚠ `applied` — не те саме, що «запит пройшов»: гонка між переглядом і
      // застосуванням (хтось інший додав запис із тим самим кодом) лишає
      // `applied: false` навіть у відповіді `200` на другий виклик. Перелік
      // перечитується й діалог закривається лише коли справді щось записано.
      if (result.applied) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(code) });
        setFile(null);
        setReport(null);
        showDone(
          t('registry.import.applied', {
            added: result.added,
            updated: result.updated,
            unchanged: result.unchanged,
          }),
        );
      }
    },
    onError: showApiError,
  });

  const applyLoading = usePendingLoading(apply.isPending);

  // ⛔ Головна умова блокування: хоч одна помилка рядка в перегляді — сервер
  // не запише нічого, навіть якщо натиснути «Застосувати». Кнопка нижче має
  // залишатися недоступною РІВНО за цієї умови.
  const blocked = (reviewed?.errors.length ?? 0) > 0;

  function close(): void {
    setReport(null);
    setFile(null);
  }

  return (
    <>
      {/* Прихований `input[type=file]` за кнопкою — той самий прийом, що в
          `ImportPanel.tsx`: рідний елемент дає діалог вибору файлу і працює
          з клавіатури, кнопка лише натискає його. */}
      <input
        ref={picker}
        type="file"
        accept=".csv"
        hidden
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const selected = event.currentTarget.files?.[0];
          if (selected !== undefined) preview.mutate({ code: registryCode, selected });

          // Дозволяє обрати той самий файл удруге: без скидання повторний
          // вибір не викликає `change`.
          event.currentTarget.value = '';
        }}
      />

      <Button
        variant="default"
        disabled={disabled}
        loading={previewLoading}
        onClick={() => picker.current?.click()}
      >
        {t('registry.import.pick')}
      </Button>

      <Modal opened={reviewed !== null} onClose={close} title={t('registry.import.title')} size="lg">
        {reviewed !== null && (
          <Stack gap="sm">
            <Group gap="xs">
              <Badge variant="light">{t('registry.import.added', { count: reviewed.added })}</Badge>
              <Badge variant="light">{t('registry.import.updated', { count: reviewed.updated })}</Badge>
              <Badge variant="light">
                {t('registry.import.unchanged', { count: reviewed.unchanged })}
              </Badge>
              {reviewed.errors.length > 0 && (
                <Badge color="statusError">
                  {t('registry.import.errorsCount', { count: reviewed.errors.length })}
                </Badge>
              )}
            </Group>

            {blocked && (
              // ⛔ Застосування заблоковане ЦІЛКОМ, а не «застосуємо решту»:
              // сервер відповідає тим самим «усе-або-нічого», і імітувати тут
              // часткове застосування означало б показати успіх там, де
              // сервер відмовить.
              <Alert color="statusWarning" title={t('registry.import.blockedTitle')}>
                {t('registry.import.blockedHint')}
              </Alert>
            )}

            {reviewed.errors.length > 0 && (
              <Table striped withTableBorder className="ecr-sticky-head">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('registry.import.row')}</Table.Th>
                    <Table.Th>{t('registry.import.entryKey')}</Table.Th>
                    <Table.Th>{t('registry.import.field')}</Table.Th>
                    <Table.Th>{t('registry.import.reason')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {reviewed.errors.map((error, index) => (
                    <Table.Tr
                      key={`${String(error.row)}:${error.key}:${error.field ?? ''}:${String(index)}`}
                    >
                      <Table.Td>{error.row}</Table.Td>
                      <Table.Td>{error.key}</Table.Td>
                      <Table.Td>{error.field ?? '—'}</Table.Td>

                      {/* ⛔ `messageKey` — ключ КАТАЛОГУ клієнта, не готовий
                          текст (аналогічно серверним кодам помилок): рендерити
                          лише через `t()`. Сирий ключ на екрані виглядав би як
                          `err.registryImport.duplicateCode` замість речення. */}
                      <Table.Td>{t(error.messageKey, { ...error.params })}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={close}>
                {t('common.cancel')}
              </Button>
              <Button
                disabled={blocked}
                loading={applyLoading}
                onClick={() => {
                  if (apply.isPending) return;
                  apply.mutate();
                }}
              >
                {t('registry.import.apply')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </>
  );
}
