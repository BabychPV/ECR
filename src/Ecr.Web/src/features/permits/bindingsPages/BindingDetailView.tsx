import type { JSX } from 'react';
import { Box, Code, Group, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useParams, useSearchParams } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import { fetchTemplateCard } from '@/features/templates/templateApi';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary, EmptyState } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { KeyValue } from '@/shared/ui/KeyValue';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { getPermitBinding } from '../api/bindingsApi';
import type { PermitBinding } from '../api/bindingsTypes';
import { PermitsPaths } from '../shell/permitPaths';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { modeLabel, roleLabel, roleNote, ruleLabel } from './bindingLabels';
import { BindingsViewPermission } from './BindingsListView';
import { parseBindingLocation, templateEditorPath } from './scanModel';
import { fetchVersionsBatch } from './useBindingsScan';

/**
 * Екран прив'язки (`/admin/permits/bindings/:id`, макет `screens-permits.js` §6): стрілка «таблиця аркуша ← таблиця
 * дозволу», режим і правило вибору, зіставлення колонок з ролями. Лише читання.
 *
 * ⚠ `:id` - код прив'язки; версія шаблону й шаблон - у параметрах адреси (`?template=&version=`), бо код сам не вказує
 * на версію. Без них екран не вгадує версію, а каже, що посилання неповне.
 * ⛔ Попереднього перегляду аркуша (`…/preview`) тут немає: він живе на вкладці Permit версії шаблону.
 */
export function BindingDetailView(): JSX.Element {
  const { id } = useParams();
  const [search] = useSearchParams();
  const session = useSession();
  const code = id ?? '';
  const location = parseBindingLocation(search);
  const mayView = can(session.data, BindingsViewPermission);

  const binding = useQuery({
    queryKey: ['permits', 'binding', location?.versionId, code],
    queryFn: () => getPermitBinding(location?.versionId ?? 0, code),
    enabled: location !== null && code.length > 0 && mayView,
  });
  // Назва шаблону й стан версії - лише підпис: їхня відмова не ховає саму прив'язку.
  const card = useQuery({
    queryKey: ['permits', 'bindingTemplate', location?.templateId],
    queryFn: () => fetchTemplateCard(location?.templateId ?? 0),
    enabled: location !== null && mayView,
  });
  const versionIds = location === null ? [] : [location.templateId];
  const versions = useQuery({
    queryKey: queryKeys.templates.versionsBatch(versionIds),
    queryFn: () => fetchVersionsBatch(versionIds),
    enabled: location !== null && mayView,
  });
  const version = versions.data
    ?.find((entry) => entry.templateId === location?.templateId)
    ?.versions.find((candidate) => candidate.id === location?.versionId);

  const back = { label: t('permits.back.bindings'), href: PermitsPaths.bindings };

  if (location === null || code.length === 0) {
    return (
      <>
        <PageHeader title={t('permits.binding.title')} back={back} />
        <PermitsSubnav current="bindings" />
        <EmptyState
          title={t('permits.bindingView.incomplete.title')}
          hint={t('permits.bindingView.incomplete.hint')}
        />
      </>
    );
  }

  if (session.data !== undefined && !mayView) {
    return (
      <>
        <PageHeader title={t('permits.binding.title')} back={back} />
        <PermitsSubnav current="bindings" />
        <EmptyState title={t('permits.bindingsList.noAccess.title')} hint={t('permits.bindingsList.noAccess.hint')} />
      </>
    );
  }

  const templateName = card.data === undefined ? '' : localized(card.data.nameL10n);
  const templateLabel = card.data === undefined ? String(location.templateId) : templateName.length > 0 ? templateName : card.data.code;
  const editor = templateEditorPath(location.templateId, location.versionId);

  return (
    <>
      <PageHeader
        title={binding.data?.sheetTableCode ?? t('permits.binding.title')}
        back={back}
        meta={binding.data === undefined ? code : <Code>{code}</Code>}
        badge={version === undefined ? undefined : <StatusBadge kind="version" state={version.status} quiet />}
        secondary={[{ label: t('permits.bindingView.openTemplate'), href: editor }]}
      />
      <PermitsSubnav current="bindings" />
      <AsyncBoundary
        isPending={binding.isPending || !mayView}
        error={binding.error}
        data={binding.data}
        onRetry={() => void binding.refetch()}
        skeleton="form"
      >
        {(data) => (
          <Stack gap="lg" data-binding-detail="">
            <Banner tone="info" text={t('permits.bindingView.readOnly')} />
            <Summary binding={data} templateLabel={templateLabel} version={version?.version} />
            <Mapping binding={data} />
          </Stack>
        )}
      </AsyncBoundary>
    </>
  );
}

function Summary({
  binding,
  templateLabel,
  version,
}: {
  readonly binding: PermitBinding;
  readonly templateLabel: string;
  readonly version: string | undefined;
}): JSX.Element {
  return (
    <Stack gap="sm">
      <Group gap="xs" wrap="wrap" data-binding-arrow="">
        <Code>{binding.sheetTableCode}</Code>
        <Text component="span" aria-label={t('permits.bindingView.reads')}>
          {'←'}
        </Text>
        <Code>{`${binding.permitTypeCode} › ${binding.permitTableCode}`}</Code>
      </Group>
      <KeyValue
        wide
        items={[
          { label: t('permits.bindingView.fact.template'), value: templateLabel },
          { label: t('permits.bindingView.fact.version'), value: version, mono: true },
          { label: t('permits.bindingView.fact.mode'), value: modeLabel(binding.mode) },
          { label: t('permits.bindingView.fact.pick'), value: ruleLabel(binding.selectionRule) },
          { label: t('permits.bindingView.fact.header'), value: binding.headerFieldCode ?? undefined, mono: true },
          {
            label: t('permits.bindingView.fact.active'),
            value: binding.isActive ? t('permits.bindingView.active.true') : t('permits.bindingView.active.false'),
          },
        ]}
      />
    </Stack>
  );
}

function Mapping({ binding }: { readonly binding: PermitBinding }): JSX.Element {
  if (binding.mode === 'Formulas') {
    return (
      <Stack gap="xs" component="section" aria-labelledby="binding-formulas-h">
        <Text component="h2" id="binding-formulas-h" fw={600}>
          {t('permits.bindingView.formulas.title')}
        </Text>
        <Text size="sm">{t('permits.bindingView.formulas.note')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs" component="section" aria-labelledby="binding-mapping-h">
      <Text component="h2" id="binding-mapping-h" fw={600}>
        {t('permits.bindingView.mapping.title')}
      </Text>
      {binding.columns.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('permits.bindingView.mapping.empty')}
        </Text>
      ) : (
        <Box style={{ overflowX: 'auto' }}>
          <Table data-binding-mapping="">
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">{t('permits.bindingView.col.sheet')}</Table.Th>
                <Table.Th scope="col">{t('permits.bindingView.col.permit')}</Table.Th>
                <Table.Th scope="col">{t('permits.bindingView.col.role')}</Table.Th>
                <Table.Th scope="col">{t('permits.bindingView.col.onSheet')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...binding.columns]
                .sort((a, b) => a.ordinal - b.ordinal)
                .map((column) => (
                  <Table.Tr key={`${column.ordinal}:${column.sheetColumnCode}`} data-binding-column={column.sheetColumnCode}>
                    <Table.Td>
                      <Code>{column.sheetColumnCode}</Code>
                    </Table.Td>
                    <Table.Td>
                      <Code>{column.permitFieldCode}</Code>
                    </Table.Td>
                    <Table.Td>{roleLabel(column.role)}</Table.Td>
                    <Table.Td>{roleNote(column.role)}</Table.Td>
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        </Box>
      )}
    </Stack>
  );
}
