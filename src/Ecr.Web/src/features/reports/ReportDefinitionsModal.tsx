import { useState, type JSX } from 'react';
import {
  ActionIcon,
  Badge,
  Button,
  Divider,
  Group,
  Modal,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  CreateReportDefRequest,
  CreateReportVersionRequest,
  ReportColumnCommand,
  ReportDefinition,
} from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * Описи звітів: перелік, заведення нового, нова версія, публікація
 * (`ФВ-10.4`, директива №09 `W7`).
 *
 * ⛔ Це **не** конструктор звітів. Веб-переглядач і конструктор ТЗ виносить за
 * обсяг (`ФВ-10.6`): рендеринг лишається в SSRS (`D-52`). Тут заводиться
 * рядок ДАНИХ — код, назва, колонки, — без якого побудова зрізу не має за що
 * зачепитися. Вигляду звіту, груп, підсумків і сортувань тут немає навмисно:
 * усе це живе в RDL.
 *
 * ⛔ Причина, чому екран узагалі знадобився: `rpt.ReportDef` не створювало
 * НІЩО — ні код, ні seed, ні тести. Кнопка «Побудувати зріз» стояла на
 * сторінці й відмовляла `ECR-RPT-0404` на будь-який код, який людина могла
 * ввести, бо описів у базі не було жодного.
 *
 * ⚠ Версія створюється ЧЕРНЕТКОЮ і публікується окремо. Побудова бере лише
 * опубліковане: опис звіту правлять саме тоді, коли ще не впевнені в ньому, і
 * зріз за чернеткою потрапив би в регуляторну вʼюху нарівні зі справжнім.
 */
export function ReportDefinitionsModal({
  opened,
  onClose,
  definitions,
}: {
  opened: boolean;
  onClose: () => void;
  definitions: readonly ReportDefinition[];
}): JSX.Element {
  const queryClient = useQueryClient();

  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});
  const [isRegulatory, setIsRegulatory] = useState(true);
  const [version, setVersion] = useState('1.0');
  const [columns, setColumns] = useState<ReportColumnCommand[]>([emptyColumn()]);

  const [versionOf, setVersionOf] = useState<string | null>(null);
  const [nextVersion, setNextVersion] = useState('');

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['report-defs'] });
  };

  const create = useMutation({
    mutationFn: () =>
      apiFetch<ReportDefinition>('/api/v1/reports', {
        method: 'POST',
        body: JSON.stringify({
          code: code.trim(),
          nameL10n: name,
          isRegulatory,
          version: version.trim(),
          columns: columns.map((column) => ({ code: column.code.trim(), kind: column.kind })),

          // ⚠ `null` — не забудькуватість: обробник підставляє єдине джерело
          // рядків, яке будівник зрізу справді вміє. Слати сюди щось інше
          // означало б обіцяти вибір, якого немає.
          rules: null,
        } satisfies CreateReportDefRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setCode('');
      setName({});
      setColumns([emptyColumn()]);
      showDone(t('reportDefs.added'));
    },
    onError: showApiError,
  });

  const addVersion = useMutation({
    mutationFn: () => {
      const parent = definitions.find((d) => String(d.id) === versionOf);

      return apiFetch<unknown>(`/api/v1/reports/${versionOf ?? ''}/versions`, {
        method: 'POST',
        body: JSON.stringify({
          version: nextVersion.trim(),

          // ⚠ Колонки беруться з ОСТАННЬОЇ версії цього ж опису, а не
          // порожні: нова версія майже завжди — та сама форма з правкою, і
          // набирати п'ять кодів наново означало б помилитися в одному з них.
          columns: columnsOf(parent),
          rules: null,
        } satisfies CreateReportVersionRequest),
      });
    },
    onSuccess: async () => {
      await refresh();
      setNextVersion('');
      showDone(t('reportDefs.versionAdded'));
    },
    onError: showApiError,
  });

  const publish = useMutation({
    mutationFn: (target: { definitionId: number; versionId: number }) =>
      apiFetch<unknown>(
        `/api/v1/reports/${target.definitionId}/versions/${target.versionId}/publish`,
        { method: 'POST' },
      ),
    onSuccess: async () => {
      await refresh();
      showDone(t('reportDefs.published'));
    },
    onError: showApiError,
  });

  const cannotCreate =
    code.trim().length === 0 ||
    !hasAnyText(name) ||
    version.trim().length === 0 ||
    columns.length === 0 ||
    columns.some((column) => column.code.trim().length === 0);

  return (
    <Modal opened={opened} onClose={onClose} title={t('reportDefs.title')} size="lg">
      <Stack gap="md">
        {definitions.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('reportDefs.empty')}
          </Text>
        ) : (
          <Table striped>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('reportDefs.code')}</Table.Th>
                <Table.Th>{t('registries.name')}</Table.Th>
                <Table.Th>{t('reportDefs.versions')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {definitions.map((definition) => (
                <Table.Tr key={definition.id}>
                  <Table.Td>
                    {definition.code}
                    {definition.isRegulatory && (
                      <Badge ml="xs" size="xs" variant="light">
                        {t('reportDefs.regulatory')}
                      </Badge>
                    )}
                    {!definition.isActive && (
                      <Badge ml="xs" size="xs" color="gray" variant="light">
                        {t('reportDefs.inactive')}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{localized(definition.nameL10n)}</Table.Td>
                  <Table.Td>
                    <Stack gap="xs">
                      {definition.versions.length === 0 && (
                        <Text size="xs" c="dimmed">
                          {t('reportDefs.noVersions')}
                        </Text>
                      )}
                      {definition.versions.map((reportVersion) => (
                        <Group key={reportVersion.id} gap="xs">
                          <Text size="xs">{reportVersion.version}</Text>
                          <Badge
                            size="xs"
                            variant="light"
                            color={reportVersion.status === 'Published' ? 'green' : 'gray'}
                          >
                            {reportVersion.status}
                          </Badge>
                          {reportVersion.status === 'Draft' && (
                            <Button
                              size="compact-xs"
                              variant="light"
                              loading={publish.isPending}
                              onClick={() =>
                                publish.mutate({
                                  definitionId: definition.id,
                                  versionId: reportVersion.id,
                                })
                              }
                            >
                              {t('reportDefs.publish')}
                            </Button>
                          )}
                        </Group>
                      ))}
                    </Stack>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}

        <Divider label={t('reportDefs.add')} />

        <TextInput
          label={t('reportDefs.code')}
          description={t('reportDefs.codeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
        />

        <LocalizedInput label={t('registries.name')} value={name} onChange={setName} />

        <Switch
          label={t('reportDefs.regulatory')}
          description={t('reportDefs.regulatoryHint')}
          checked={isRegulatory}
          onChange={(event) => setIsRegulatory(event.currentTarget.checked)}
        />

        <TextInput
          label={t('reportDefs.version')}
          description={t('reportDefs.versionHint')}
          value={version}
          onChange={(event) => setVersion(event.currentTarget.value)}
        />

        <ColumnsEditor columns={columns} onChange={setColumns} />

        <Group justify="flex-end">
          <Button disabled={cannotCreate} loading={create.isPending} onClick={() => create.mutate()}>
            {t('reportDefs.add')}
          </Button>
        </Group>

        <Divider label={t('reportDefs.newVersion')} />

        <Select
          label={t('reportDefs.forReport')}
          description={t('reportDefs.forReportHint')}
          data={definitions.map((definition) => ({
            value: String(definition.id),
            label: `${definition.code} · ${localized(definition.nameL10n)}`,
          }))}
          value={versionOf}
          onChange={setVersionOf}
        />

        <TextInput
          label={t('reportDefs.version')}
          value={nextVersion}
          onChange={(event) => setNextVersion(event.currentTarget.value)}
        />

        <Group justify="flex-end">
          <Button
            variant="default"
            disabled={versionOf === null || nextVersion.trim().length === 0}
            loading={addVersion.isPending}
            onClick={() => addVersion.mutate()}
          >
            {t('reportDefs.newVersion')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Редактор колонок зрізу.
 *
 * ⚠ Код колонки — це ключ рядка зрізу (`rpt.ReportRow.ColumnCode`), тобто те,
 * за чим SSRS шукає значення. Тому він набирається, а не вибирається: перелік
 * можливих кодів задає не інтерфейс, а те, що будівник зрізу пише в рядок.
 */
function ColumnsEditor({
  columns,
  onChange,
}: {
  columns: readonly ReportColumnCommand[];
  onChange: (next: ReportColumnCommand[]) => void;
}): JSX.Element {
  return (
    <Stack gap="xs">
      <Text size="sm" fw={500}>
        {t('reportDefs.columns')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('reportDefs.columnsHint')}
      </Text>

      {columns.map((column, index) => (
        // Індекс як ключ — свідомо: у колонки немає власного
        // ідентифікатора, доки версію не записано, а код змінюється прямо
        // під час набору і ключем бути не може.
        <Group key={index} gap="xs" align="end">
          <TextInput
            label={index === 0 ? t('reportDefs.columnCode') : undefined}
            value={column.code}
            onChange={(event) =>
              onChange(
                columns.map((c, i) => (i === index ? { ...c, code: event.currentTarget.value } : c)),
              )
            }
          />
          <Select
            label={index === 0 ? t('reportDefs.columnKind') : undefined}
            miw={120}
            allowDeselect={false}
            data={ColumnKinds}
            value={column.kind}
            onChange={(value) =>
              onChange(columns.map((c, i) => (i === index ? { ...c, kind: value ?? c.kind } : c)))
            }
          />
          <ActionIcon
            variant="subtle"
            color="statusError"
            aria-label={t('reportDefs.removeColumn')}
            disabled={columns.length === 1}
            onClick={() => onChange(columns.filter((_, i) => i !== index))}
          >
            ×
          </ActionIcon>
        </Group>
      ))}

      <Group>
        <Button
          size="xs"
          variant="default"
          onClick={() => onChange([...columns, emptyColumn()])}
        >
          {t('reportDefs.addColumn')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Типи значення комірки зрізу.
 *
 * ⛔ Рівно три, і не з міркувань смаку: рядок зрізу має рівно три колонки
 * значення (`ValueString`, `ValueNumeric`, `ValueDate`). Четвертому типові
 * нема куди лягти, і сервер відхиляє його тим самим переліком.
 */
const ColumnKinds = ['text', 'number', 'date'];

/** Порожній рядок редактора колонок. */
function emptyColumn(): ReportColumnCommand {
  return { code: '', kind: 'text' };
}

/**
 * Колонки останньої версії опису — початковий стан нової версії.
 *
 * ⚠ Розбір не валить форму: зламаний `columnsJson` означає опис, заведений
 * повз API, і показати порожній перелік чесніше, ніж не відкрити діалог.
 */
function columnsOf(definition: ReportDefinition | undefined): ReportColumnCommand[] {
  const latest = definition?.versions[0];
  if (latest === undefined) return [];

  try {
    const parsed: unknown = JSON.parse(latest.columnsJson);

    return Array.isArray(parsed) ? (parsed as ReportColumnCommand[]) : [];
  } catch {
    return [];
  }
}
