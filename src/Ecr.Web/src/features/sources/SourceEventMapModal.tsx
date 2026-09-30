import { useMemo, useState, type JSX } from 'react';
import {
  Button,
  Divider,
  Group,
  List,
  Loader,
  Modal,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { useReturnFocusOnUnmount } from '@/shared/a11y/focus';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { volumeModeLabel } from './SourceEventMapsPanel';
import { SourceEventProbePanel, type ProbeValueMapField } from './SourceEventProbePanel';
import {
  emptyForm,
  EndAttribute,
  formFromMap,
  isNumericColumn,
  isReserved,
  newFieldRow,
  NameAttribute,
  StartAttribute,
  toCreateRequest,
  toUpdateRequest,
  validateForm,
  valueKindsFor,
  VolumeModes,
  type AttributeScope,
  type FieldRow,
  type MapColumn,
  type MapFormProblem,
  type MapFormState,
  type ValueKind,
  type ValuePair,
} from './sourceEventMapForm';
import {
  createSourceEventMap,
  fetchEventTemplates,
  updateSourceEventMap,
  type SourceEventMap,
} from './sourceEventsApi';
import {
  fetchDynamicTables,
  fetchRegistries,
  fetchRegistryEntries,
  fetchTableColumns,
  fetchUnits,
  isEventsNotConfigured,
  SourceEventsKeys,
} from './sourceEventsData';

/** Роздільник «область:ім'я» у значенні вибору атрибута; в області двокрапки немає, тож ім'я може її мати. */
function attributeValue(scope: AttributeScope, name: string): string {
  return `${scope}:${name}`;
}

function parseAttributeValue(value: string): { scope: AttributeScope; name: string } {
  const at = value.indexOf(':');
  const scope = value.slice(0, at) === 'PrimaryElement' ? 'PrimaryElement' : 'Event';

  return { scope, name: value.slice(at + 1) };
}

/** Текст проблеми форми — літералами, щоб сторож каталогу бачив кожен ключ. */
export function problemLabel(problem: MapFormProblem): string {
  switch (problem) {
    case 'documentRequired':
      return t('sourceEvents.problem.documentRequired');
    case 'tableRequired':
      return t('sourceEvents.problem.tableRequired');
    case 'startEndRequired':
      return t('sourceEvents.problem.startEndRequired');
    case 'startEndNotDate':
      return t('sourceEvents.problem.startEndNotDate');
    case 'fieldIncomplete':
      return t('sourceEvents.problem.fieldIncomplete');
    case 'duplicateColumn':
      return t('sourceEvents.problem.duplicateColumn');
    case 'valueKindMismatch':
      return t('sourceEvents.problem.valueKindMismatch');
    case 'valueMapIncomplete':
      return t('sourceEvents.problem.valueMapIncomplete');
    case 'filterIncomplete':
      return t('sourceEvents.problem.filterIncomplete');
  }
}

function valueKindLabel(kind: ValueKind): string {
  switch (kind) {
    case 'Direct':
      return t('sourceEvents.valueDirect');
    case 'LookupByCode':
      return t('sourceEvents.valueByCode');
    case 'LookupByName':
      return t('sourceEvents.valueByName');
    case 'ValueMap':
      return t('sourceEvents.valueMap');
  }
}

function scopeLabel(scope: AttributeScope): string {
  return scope === 'Event' ? t('sourceEvents.scopeEvent') : t('sourceEvents.scopePrimaryElement');
}

function reservedLabel(name: string): string {
  if (name === StartAttribute) return t('sourceEvents.attrStart');
  if (name === EndAttribute) return t('sourceEvents.attrEnd');
  return t('sourceEvents.attrName');
}

/** Редактор пар «значення PI → запис довідника» для поля з `ValueMap`. */
function ValueMapEditor({
  registryCode,
  pairs,
  onChange,
}: {
  readonly registryCode: string | null;
  readonly pairs: readonly ValuePair[];
  readonly onChange: (pairs: ValuePair[]) => void;
}): JSX.Element {
  const entries = useQuery({
    queryKey: SourceEventsKeys.entries(registryCode ?? ''),
    queryFn: () => fetchRegistryEntries(registryCode ?? ''),
    enabled: registryCode !== null,
  });

  const options = (entries.data ?? []).map((entry) => ({
    value: String(entry.id),
    label: `${entry.code} · ${entry.display}`,
  }));

  const replace = (index: number, pair: ValuePair): void => onChange(pairs.map((item, i) => (i === index ? pair : item)));

  return (
    <Stack gap="xs" data-source-event-value-map="">
      {entries.isError && <ErrorAlert error={entries.error} onRetry={() => void entries.refetch()} />}
      {pairs.map((pair, index) => (
        <Group key={index} gap="xs" wrap="nowrap" data-source-event-value-pair={index}>
          <TextInput
            size="xs"
            aria-label={t('sourceEvents.valueMapSource')}
            placeholder={t('sourceEvents.valueMapSource')}
            value={pair.sourceValue}
            onChange={(event) => replace(index, { ...pair, sourceValue: event.currentTarget.value })}
          />
          <Select
            size="xs"
            searchable
            aria-label={t('sourceEvents.valueMapEntry')}
            placeholder={t('sourceEvents.valueMapEntry')}
            data={options}
            value={pair.registryEntryId === null ? null : String(pair.registryEntryId)}
            onChange={(value) => replace(index, { ...pair, registryEntryId: value === null ? null : Number(value) })}
          />
          <Button
            size="compact-xs"
            variant="subtle"
            color="statusError"
            aria-label={`${t('sourceEvents.remove')}: ${pair.sourceValue || String(index + 1)}`}
            onClick={() => onChange(pairs.filter((_, i) => i !== index))}
          >
            {t('sourceEvents.remove')}
          </Button>
        </Group>
      ))}
      <Button
        size="compact-xs"
        variant="subtle"
        onClick={() => onChange([...pairs, { sourceValue: '', registryEntryId: null }])}
        data-source-event-value-add=""
      >
        {t('sourceEvents.valueMapAdd')}
      </Button>
    </Stack>
  );
}

/**
 * Форма мапінгу подій (HSE301 A6, FEATURE-HSE301-VIEW §4.7.3, §10.6): документ і динамічна таблиця, режим
 * об'єму, звуження, сітка «колонка ↔ атрибут» з каталогу шаблону, спосіб запису значення, одиниці, таблиця
 * відповідностей і проба на реальних подіях.
 *
 * ⛔ Атрибути — лише з каталогу (`event-templates`) і три зарезервовані (`$start`, `$end`, `$name`): ручного
 * введення імені немає (§10.6). Атрибут мапінгу, якого в каталозі вже немає, лишається у виборі — інакше форма
 * тихо викинула б його при збереженні.
 *
 * ⚠ Документ і таблицю змінити в наявному мапінгу не можна (`PUT` їх не приймає) — поля вимкнені.
 */
export function SourceEventMapModal({
  dataSourceId,
  sourceEntityId,
  template,
  map,
  documents,
  onClose,
}: {
  readonly dataSourceId: number;
  readonly sourceEntityId: number;
  /** Ім'я шаблону подій = код сутності. */
  readonly template: string;
  /** `null` — новий мапінг. */
  readonly map: SourceEventMap | null;
  readonly documents: readonly DocumentSummary[];
  readonly onClose: () => void;
}): JSX.Element {
  // ⛔ Діалог монтується за умовою (`SourceEventsTab`: `editing !== null && …`), а `Modal` повертає фокус лише
  // на зміну `opened` — розмонтування його не повертає, і після «Скасувати»/«Зберегти» фокус падав на `<body>`.
  useReturnFocusOnUnmount();
  const queryClient = useQueryClient();
  const [state, setState] = useState<MapFormState>(() => (map === null ? emptyForm() : formFromMap(map)));
  const patch = (next: Partial<MapFormState>): void => setState((current) => ({ ...current, ...next }));
  const patchField = (key: string, next: Partial<FieldRow>): void =>
    setState((current) => ({
      ...current,
      fields: current.fields.map((field) => (field.key === key ? { ...field, ...next } : field)),
    }));

  const templates = useQuery({
    queryKey: SourceEventsKeys.templates(dataSourceId),
    queryFn: () => fetchEventTemplates(dataSourceId),
    retry: false,
  });

  const tables = useQuery({
    queryKey: SourceEventsKeys.tables(state.documentId ?? 0),
    queryFn: () => fetchDynamicTables(state.documentId ?? 0),
    enabled: state.documentId !== null,
  });

  const table = tables.data?.find((item) => item.tableDefId === state.tableDefId);

  const columns = useQuery({
    queryKey: SourceEventsKeys.columns(state.documentId ?? 0, table?.tableInstanceId ?? 0),
    queryFn: () => fetchTableColumns(state.documentId ?? 0, table?.tableInstanceId ?? 0),
    enabled: state.documentId !== null && table !== undefined,
  });

  const units = useQuery({ queryKey: SourceEventsKeys.units, queryFn: fetchUnits });
  const registries = useQuery({ queryKey: SourceEventsKeys.registries, queryFn: fetchRegistries });

  const catalog = templates.data?.find((item) => item.templateName.toLowerCase() === template.toLowerCase());
  const notConfigured = templates.isError && isEventsNotConfigured(templates.error);

  const columnList: readonly MapColumn[] = columns.data ?? [];
  const columnById = new Map(columnList.map((column) => [column.id, column]));

  /** Атрибути для вибору: зарезервовані, каталог шаблону і ті, що вже стоять у мапінгу. */
  const attributeOptions = useMemo(() => {
    const options = new Map<string, string>();
    for (const name of [StartAttribute, EndAttribute, NameAttribute]) {
      options.set(attributeValue('Event', name), `${name} · ${reservedLabel(name)}`);
    }
    for (const attribute of catalog?.attributes ?? []) {
      const unit = attribute.sourceUnitSymbol === null ? '' : ` [${attribute.sourceUnitSymbol}]`;
      options.set(attributeValue(attribute.scope, attribute.name), `${attribute.name}${unit} · ${scopeLabel(attribute.scope)}`);
    }
    for (const field of state.fields) {
      if (field.sourceAttribute.length === 0) continue;
      const value = attributeValue(isReserved(field.sourceAttribute) ? 'Event' : field.attributeScope, field.sourceAttribute);
      if (!options.has(value)) options.set(value, `${field.sourceAttribute} · ${scopeLabel(field.attributeScope)}`);
    }
    if (state.filterAttribute.length > 0 && state.filterScope !== null) {
      const value = attributeValue(state.filterScope, state.filterAttribute);
      if (!options.has(value)) options.set(value, `${state.filterAttribute} · ${scopeLabel(state.filterScope)}`);
    }

    return [...options].map(([value, label]) => ({ value, label }));
  }, [catalog, state.fields, state.filterAttribute, state.filterScope]);

  const catalogOptions = attributeOptions.filter((option) => !isReserved(parseAttributeValue(option.value).name));

  const unitOptions = (units.data ?? []).map((unit) => ({ value: String(unit.id), label: unit.code }));
  const registryCode = (id: number | null): string | null =>
    id === null ? null : (registries.data?.find((registry) => registry.id === id)?.code ?? null);

  const problems = validateForm(state, columnList);

  const save = useMutation({
    mutationFn: () =>
      map === null
        ? createSourceEventMap(toCreateRequest(sourceEntityId, state))
        : updateSourceEventMap(map.id, toUpdateRequest(state)),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.maps(sourceEntityId) });
      void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.eventsOf(sourceEntityId) });
      showDone(map === null ? t('sourceEvents.mapCreated') : t('sourceEvents.mapSaved'));
      onClose();
    },
  });

  const probeAttributes = state.fields
    .filter((field) => field.sourceAttribute.length > 0 && !isReserved(field.sourceAttribute))
    .map((field) => ({ name: field.sourceAttribute, scope: field.attributeScope }));

  const valueMapFields: ProbeValueMapField[] = state.fields
    .filter((field) => field.valueKind === 'ValueMap' && field.sourceAttribute.length > 0)
    .map((field) => ({
      key: field.key,
      attribute: field.sourceAttribute,
      scope: field.attributeScope,
      known: field.values.map((pair) => pair.sourceValue),
    }));

  return (
    <Modal
      opened
      onClose={onClose}
      size="90rem"
      title={map === null ? t('sourceEvents.mapCreateTitle') : t('sourceEvents.mapEditTitle')}
    >
      <Stack gap="sm" data-source-event-map-form="">
        {notConfigured && (
          <Banner
            tone="info"
            title={t('sourceEvents.notConfiguredTitle')}
            text={problemText(templates.error).detail ?? t('sourceEvents.notConfiguredText')}
            testId="source-event-map-not-configured"
          />
        )}
        {templates.isError && !notConfigured && (
          <ErrorAlert error={templates.error} onRetry={() => void templates.refetch()} />
        )}
        {templates.isSuccess && catalog === undefined && (
          <Banner tone="warning" text={t('sourceEvents.templateMissing', { template })} testId="source-event-template-missing" />
        )}

        <Group grow align="start">
          <Select
            label={t('sourceEvents.document')}
            searchable
            required
            disabled={map !== null}
            data={documents.map((document) => ({ value: String(document.id), label: document.businessKey }))}
            value={state.documentId === null ? null : String(state.documentId)}
            onChange={(value) => patch({ documentId: value === null ? null : Number(value), tableDefId: null })}
            data-source-event-map-document=""
          />
          <Select
            label={t('sourceEvents.table')}
            description={t('sourceEvents.tableHint')}
            required
            disabled={map !== null || state.documentId === null}
            data={(tables.data ?? []).map((item) => ({
              value: String(item.tableDefId),
              label: `${item.tableCode} · ${item.sheetCode}`,
            }))}
            value={state.tableDefId === null ? null : String(state.tableDefId)}
            onChange={(value) => patch({ tableDefId: value === null ? null : Number(value) })}
            data-source-event-map-table=""
          />
        </Group>
        {tables.isError && <ErrorAlert error={tables.error} onRetry={() => void tables.refetch()} />}
        {tables.isSuccess && tables.data.length === 0 && (
          <Text size="sm" c="dimmed">
            {t('sourceEvents.noDynamicTables')}
          </Text>
        )}
        {columns.isError && <ErrorAlert error={columns.error} onRetry={() => void columns.refetch()} />}

        <Group grow align="end">
          <Select
            label={t('sourceEvents.filterAttribute')}
            description={t('sourceEvents.filterAttributeHint')}
            clearable
            data={catalogOptions}
            value={
              state.filterAttribute.length > 0 && state.filterScope !== null
                ? attributeValue(state.filterScope, state.filterAttribute)
                : null
            }
            onChange={(value) => {
              if (value === null) {
                patch({ filterAttribute: '', filterScope: null, filterValue: '' });
                return;
              }
              const parsed = parseAttributeValue(value);
              patch({ filterAttribute: parsed.name, filterScope: parsed.scope });
            }}
            data-source-event-map-filter-attribute=""
          />
          <TextInput
            label={t('sourceEvents.filterValue')}
            value={state.filterValue}
            onChange={(event) => patch({ filterValue: event.currentTarget.value })}
            data-source-event-map-filter-value=""
          />
        </Group>

        <Select
          label={t('sourceEvents.volumeMode')}
          description={t('sourceEvents.volumeModeHint')}
          allowDeselect={false}
          data={VolumeModes.map((mode) => ({ value: mode, label: volumeModeLabel(mode) }))}
          value={state.volumeMode}
          onChange={(value) => patch({ volumeMode: VolumeModes.find((mode) => mode === value) ?? 'None' })}
          data-source-event-volume=""
        />

        {map !== null && (
          <Switch
            label={t('sourceEvents.mapActiveSwitch')}
            checked={state.isActive}
            onChange={(event) => patch({ isActive: event.currentTarget.checked })}
            data-source-event-map-active=""
          />
        )}

        <Divider />
        <Title order={5}>{t('sourceEvents.fieldsTitle')}</Title>
        {columns.isFetching && <Loader size="xs" />}

        <Table.ScrollContainer minWidth={960}>
          <Table data-source-event-fields="">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('sourceEvents.fieldColumn')}</Table.Th>
                <Table.Th>{t('sourceEvents.fieldAttribute')}</Table.Th>
                <Table.Th>{t('sourceEvents.fieldScope')}</Table.Th>
                <Table.Th>{t('sourceEvents.fieldHow')}</Table.Th>
                <Table.Th>{t('sourceEvents.fieldUnits')}</Table.Th>
                <Table.Th>
                  <VisuallyHidden>{t('sourceEvents.actions')}</VisuallyHidden>
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {state.fields.map((field) => {
                const column = field.targetColumnDefId === null ? undefined : columnById.get(field.targetColumnDefId);
                const reserved = isReserved(field.sourceAttribute);
                const kinds = valueKindsFor(column);

                return (
                  <Table.Tr key={field.key} data-source-event-field={field.sourceAttribute || field.key}>
                    <Table.Td>
                      <Select
                        size="xs"
                        searchable
                        aria-label={t('sourceEvents.fieldColumn')}
                        data={columnList.map((item) => ({
                          value: String(item.id),
                          label: `${item.code} · ${item.header} (${item.dataType})`,
                        }))}
                        value={field.targetColumnDefId === null ? null : String(field.targetColumnDefId)}
                        onChange={(value) => {
                          const target = value === null ? undefined : columnById.get(Number(value));
                          patchField(field.key, {
                            targetColumnDefId: value === null ? null : Number(value),
                            valueKind: valueKindsFor(target).includes(field.valueKind) ? field.valueKind : 'Direct',
                            targetUnitId: target?.unitId ?? null,
                          });
                        }}
                        data-source-event-field-column=""
                      />
                    </Table.Td>
                    <Table.Td>
                      <Select
                        size="xs"
                        searchable
                        aria-label={t('sourceEvents.fieldAttribute')}
                        data={attributeOptions}
                        value={
                          field.sourceAttribute.length === 0
                            ? null
                            : attributeValue(reserved ? 'Event' : field.attributeScope, field.sourceAttribute)
                        }
                        onChange={(value) => {
                          if (value === null) {
                            patchField(field.key, { sourceAttribute: '' });
                            return;
                          }
                          const parsed = parseAttributeValue(value);
                          patchField(field.key, { sourceAttribute: parsed.name, attributeScope: parsed.scope });
                        }}
                        data-source-event-field-attribute=""
                      />
                    </Table.Td>
                    <Table.Td>
                      <Select
                        size="xs"
                        aria-label={t('sourceEvents.fieldScope')}
                        disabled={reserved}
                        allowDeselect={false}
                        data={[
                          { value: 'Event', label: t('sourceEvents.scopeEvent') },
                          { value: 'PrimaryElement', label: t('sourceEvents.scopePrimaryElement') },
                        ]}
                        value={reserved ? 'Event' : field.attributeScope}
                        onChange={(value) =>
                          patchField(field.key, { attributeScope: value === 'PrimaryElement' ? 'PrimaryElement' : 'Event' })
                        }
                        data-source-event-field-scope=""
                      />
                    </Table.Td>
                    <Table.Td>
                      <Select
                        size="xs"
                        aria-label={t('sourceEvents.fieldHow')}
                        allowDeselect={false}
                        data={kinds.map((kind) => ({ value: kind, label: valueKindLabel(kind) }))}
                        value={field.valueKind}
                        onChange={(value) =>
                          patchField(field.key, { valueKind: kinds.find((kind) => kind === value) ?? 'Direct' })
                        }
                        data-source-event-field-how=""
                      />
                      {field.valueKind === 'ValueMap' && (
                        <ValueMapEditor
                          registryCode={registryCode(column?.lookupRegistryDefId ?? null)}
                          pairs={field.values}
                          onChange={(values) => patchField(field.key, { values })}
                        />
                      )}
                    </Table.Td>
                    <Table.Td>
                      {isNumericColumn(column) && (
                        <Group gap="xs" wrap="nowrap">
                          <Select
                            size="xs"
                            searchable
                            clearable
                            aria-label={t('sourceEvents.sourceUnit')}
                            placeholder={t('sourceEvents.sourceUnit')}
                            data={unitOptions}
                            value={field.sourceUnitId === null ? null : String(field.sourceUnitId)}
                            onChange={(value) => patchField(field.key, { sourceUnitId: value === null ? null : Number(value) })}
                          />
                          <Select
                            size="xs"
                            searchable
                            clearable
                            aria-label={t('sourceEvents.targetUnit')}
                            placeholder={t('sourceEvents.targetUnit')}
                            data={unitOptions}
                            value={field.targetUnitId === null ? null : String(field.targetUnitId)}
                            onChange={(value) => patchField(field.key, { targetUnitId: value === null ? null : Number(value) })}
                          />
                        </Group>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        color="statusError"
                        aria-label={`${t('sourceEvents.remove')}: ${field.sourceAttribute || column?.code || String(state.fields.indexOf(field) + 1)}`}
                        onClick={() => patch({ fields: state.fields.filter((item) => item.key !== field.key) })}
                        data-source-event-field-remove=""
                      >
                        {t('sourceEvents.remove')}
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>

        <Group>
          <Button
            size="xs"
            variant="default"
            onClick={() => patch({ fields: [...state.fields, newFieldRow()] })}
            data-source-event-field-add=""
          >
            {t('sourceEvents.fieldAdd')}
          </Button>
        </Group>

        <Divider />
        <SourceEventProbePanel
          dataSourceId={dataSourceId}
          template={template}
          attributes={probeAttributes}
          valueMapFields={valueMapFields}
          onAddValue={(key, value) => {
            const field = state.fields.find((item) => item.key === key);
            if (field !== undefined) patchField(key, { values: [...field.values, { sourceValue: value, registryEntryId: null }] });
          }}
        />

        <Divider />
        {problems.length > 0 && (
          <Stack gap="xs" data-source-event-map-problems="">
            <Text size="sm" fw={600}>
              {t('sourceEvents.cannotSave')}
            </Text>
            <List size="sm">
              {problems.map((problem) => (
                <List.Item key={problem} data-source-event-map-problem={problem}>
                  {problemLabel(problem)}
                </List.Item>
              ))}
            </List>
          </Stack>
        )}
        {save.error !== null && <ErrorAlert error={save.error} />}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={problems.length > 0}
            loading={save.isPending}
            onClick={() => save.mutate()}
            data-source-event-map-save=""
          >
            {t('common.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
