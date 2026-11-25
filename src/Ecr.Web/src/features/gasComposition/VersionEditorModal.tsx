import { useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Button, Checkbox, Group, Modal, Select, SimpleGrid, Stack, Switch, Table, Text, TextInput, Title } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useMutation } from '@tanstack/react-query';
import { normalizeDecimal } from '@/shared/format';
import { formatMonthName } from '@/shared/format/period';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { showApiError } from '@/shared/ui/notify';
import {
  createVersion,
  deviates,
  monthOf,
  sumOf,
  updateVersion,
  type GasChangeResult,
  type GasComponent,
  type GasVersion,
} from './api';
import { componentName } from './componentNames';

/** Порядок груп компонентів у редакторі (як у FLERT/HYSYS: інерти, вуглеводні, ароматика, сірка, інше). */
const GroupOrder = ['inert', 'hydrocarbon', 'aromatic', 'sulphur', 'other'] as const;

/** Підпис групи — ключі літералами (сторож каталогу бачить кожен). */
const GroupLabel: Record<(typeof GroupOrder)[number], () => string> = {
  inert: () => t('gasComposition.editor.group.inert'),
  hydrocarbon: () => t('gasComposition.editor.group.hydrocarbon'),
  aromatic: () => t('gasComposition.editor.group.aromatic'),
  sulphur: () => t('gasComposition.editor.group.sulphur'),
  other: () => t('gasComposition.editor.group.other'),
};

/** Що редагується. */
export type VersionEditorTarget =
  /** Нова версія наявного кейсу (значення — копія попередньої). */
  | { readonly kind: 'new'; readonly caseName: string; readonly copyFrom: GasVersion | undefined }
  /** Перша версія нового кейсу. */
  | { readonly kind: 'newCase' }
  /** Наявна версія; `previous` — для порівняння; `readOnly` — замкнена або без права змін. */
  | {
      readonly kind: 'edit';
      readonly caseName: string;
      readonly version: GasVersion;
      readonly previous: GasVersion | undefined;
      readonly readOnly: boolean;
    };

interface Props {
  readonly streamId: number;
  readonly target: VersionEditorTarget | null;
  readonly components: readonly GasComponent[];
  readonly onClose: () => void;
  readonly onSaved: (result: GasChangeResult) => void;
}

/** `2026-03-01` → [2026, 3]. */
function splitDate(value: string | undefined): [number, number] {
  if (value === undefined) {
    const now = new Date();
    return [now.getFullYear(), now.getMonth() + 1];
  }
  const [year, month] = value.split('-').map(Number);
  return [year ?? new Date().getFullYear(), month ?? 1];
}

/** Значення в полі вводу: рядок `decimal` сервера як є (повна точність, `DecimalAsStringJsonConverter`). */
function asText(value: string | null | undefined): string {
  return value ?? '';
}

/**
 * Розбір поля: порожнє — `null`; нечисло — `undefined` (помилка); інакше — нормалізований рядок `decimal` (крапка,
 * без пробілів). ⚠ Рядок, а не `Number`: незмінене значення FLERT `decimal(20,16)` має повернутися на сервер тим самим.
 */
function parse(text: string): string | null | undefined {
  const trimmed = text.trim();
  if (trimmed.length === 0) return null;
  return normalizeDecimal(trimmed) ?? undefined;
}

/**
 * Редактор версії складу газу (D-NEW): місяць дії, властивості (T, ρ, MW, Z), компоненти мол.% групами з живою Σ і
 * колонкою «Попередня» (порівняння з попередньою версією, змінене виділено). Σ поза 100 ± 0.5 зберігається лише з явним
 * підтвердженням — те саме правило, що на сервері.
 *
 * ⚠ Дата — місяць і рік двома списками, а не календар: версія діє з 1-го числа місяця (сервер відмовляє інакше), тож
 * вибір дня був би вибором, якого немає.
 */
export function VersionEditorModal({ streamId, target, components, onClose, onSaved }: Props): JSX.Element | null {
  if (target === null) return null;
  // ⚠ `key` — новий стан форми на кожну ціль (інакше значення попередньої версії лишалися б у полях).
  const key = target.kind === 'edit' ? `edit-${String(target.version.id)}` : target.kind === 'new' ? `new-${target.caseName}` : 'new-case';
  return <Editor key={key} streamId={streamId} target={target} components={components} onClose={onClose} onSaved={onSaved} />;
}

function Editor({ streamId, target, components, onClose, onSaved }: Props & { readonly target: VersionEditorTarget }): JSX.Element {
  const narrow = useMediaQuery('(max-width: 600px)');
  const source = target.kind === 'edit' ? target.version : target.kind === 'new' ? target.copyFrom : undefined;
  const previous = target.kind === 'edit' ? target.previous : target.kind === 'new' ? target.copyFrom : undefined;
  const readOnly = target.kind === 'edit' && target.readOnly;

  const [initialYear, initialMonth] = target.kind === 'edit' ? splitDate(target.version.validFrom) : splitDate(undefined);
  const [caseName, setCaseName] = useState(target.kind === 'newCase' ? '' : target.caseName);
  const [year, setYear] = useState(String(initialYear));
  const [month, setMonth] = useState(String(initialMonth));
  const [temperature, setTemperature] = useState(asText(source?.temperatureC));
  const [density, setDensity] = useState(asText(source?.densityOp));
  const [mw, setMw] = useState(asText(source?.mwHysys));
  const [z, setZ] = useState(asText(source?.z));
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries((source?.components ?? []).map((c) => [c.code, c.molPct])),
  );
  const [onlyFilled, setOnlyFilled] = useState(readOnly);
  const [accept, setAccept] = useState(false);

  const previousValues = useMemo(
    () => new Map((previous?.components ?? []).map((c) => [c.code, c.molPct] as const)),
    [previous],
  );

  const parsed = Object.entries(values).map(([code, text]) => ({ code, value: parse(text) }));
  const invalid = new Set(parsed.filter((p) => p.value === undefined).map((p) => p.code));
  const filled = parsed.filter((p): p is { code: string; value: string } => typeof p.value === 'string' && Number(p.value) !== 0);
  const sum = sumOf(filled.map((p) => ({ molPct: p.value })));
  const off = deviates(sum);
  const properties = [temperature, density, mw, z].map(parse);
  const propertiesInvalid = properties.some((p) => p === undefined);

  const years = useMemo(() => {
    const now = new Date().getFullYear();
    const from = Math.min(2015, initialYear);
    return Array.from({ length: now + 6 - from }, (_, i) => String(from + i)).reverse();
  }, [initialYear]);

  const validFrom = `${year}-${month.padStart(2, '0')}-01`;

  const save = useMutation({
    mutationFn: () => {
      const body = {
        validFrom,
        temperatureC: properties[0] ?? null,
        densityOp: properties[1] ?? null,
        mwHysys: properties[2] ?? null,
        z: properties[3] ?? null,
        components: filled.map((p) => ({ code: p.code, molPct: p.value })),
        acceptSumDeviation: accept,
      };
      return target.kind === 'edit'
        ? updateVersion(target.version.id, body)
        : createVersion(streamId, { ...body, caseName: caseName.trim() });
    },
    onSuccess: onSaved,
    onError: showApiError,
  });
  const saveLoading = usePendingLoading(save.isPending);

  const grouped = GroupOrder.map((group) => ({
    group,
    items: components.filter(
      (c) => c.group === group && (!onlyFilled || (values[c.code] ?? '').trim().length > 0 || previousValues.has(c.code)),
    ),
  })).filter((g) => g.items.length > 0);

  const title =
    target.kind === 'newCase'
      ? t('gasComposition.editor.newCaseTitle')
      : target.kind === 'new'
        ? t('gasComposition.editor.newTitle', { case: target.caseName })
        : t('gasComposition.editor.editTitle', { from: monthOf(target.version.validFrom), case: target.caseName });

  const canSave =
    !readOnly &&
    !save.isPending &&
    caseName.trim().length > 0 &&
    invalid.size === 0 &&
    !propertiesInvalid &&
    (!off || accept);

  return (
    <Modal opened onClose={onClose} title={title} size="xl" fullScreen={narrow === true} data-testid="gas-version-editor">
      <Stack gap="md">
        {target.kind === 'newCase' && (
          <TextInput
            label={t('gasComposition.caseName')}
            value={caseName}
            maxLength={100}
            required
            data-autofocus
            onChange={(event) => setCaseName(event.currentTarget.value)}
          />
        )}

        {readOnly && target.kind === 'edit' && target.version.locks.length > 0 && (
          <Alert color="statusWarning" data-testid="gas-version-locked">
            {t('gasComposition.locked', { periods: target.version.locks.map((l) => l.period).join(', ') })} —{' '}
            {t('gasComposition.lockedHint')}
          </Alert>
        )}

        <Stack gap="xs">
          <Text fw={600} size="sm" id="gas-effective-from">
            {t('gasComposition.editor.effectiveFrom')}
          </Text>
          <Group gap="sm" align="end" role="group" aria-labelledby="gas-effective-from">
            <Select
              label={t('gasComposition.editor.month')}
              data={Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: formatMonthName(i + 1) }))}
              value={month}
              onChange={(value) => setMonth(value ?? month)}
              allowDeselect={false}
              disabled={readOnly}
              miw={170}
            />
            <Select
              label={t('gasComposition.editor.year')}
              data={years}
              value={year}
              onChange={(value) => setYear(value ?? year)}
              allowDeselect={false}
              searchable
              disabled={readOnly}
              miw={110}
            />
          </Group>
          <Text size="xs" c="dimmed">
            {t('gasComposition.editor.effectiveHint')}
          </Text>
          {target.kind === 'new' && target.copyFrom !== undefined && (
            <Text size="xs" c="dimmed" data-testid="gas-copied-from">
              {t('gasComposition.editor.copiedFrom', { from: monthOf(target.copyFrom.validFrom) })}
            </Text>
          )}
        </Stack>

        <Stack gap="xs">
          <Title order={4} size="h5">
            {t('gasComposition.editor.properties')}
          </Title>
          <SimpleGrid cols={{ base: 1, xs: 2, md: 4 }} spacing="sm">
            {(
              [
                ['temperature', t('gasComposition.editor.temperature'), temperature, setTemperature],
                ['density', t('gasComposition.editor.density'), density, setDensity],
                ['mw', t('gasComposition.editor.mw'), mw, setMw],
                ['z', t('gasComposition.editor.z'), z, setZ],
              ] as const
            ).map(([name, label, value, set], index) => (
              <TextInput
                key={name}
                label={label}
                inputMode="decimal"
                value={value}
                readOnly={readOnly}
                error={properties[index] === undefined ? t('gasComposition.editor.invalidNumber') : undefined}
                onChange={(event) => set(event.currentTarget.value)}
              />
            ))}
          </SimpleGrid>
        </Stack>

        <Group justify="space-between" align="center" wrap="wrap" gap="sm">
          <Title order={4} size="h5">
            {t('gasComposition.editor.components')}
          </Title>
          <Group gap="md">
            <Switch
              label={t('gasComposition.editor.onlyFilled')}
              checked={onlyFilled}
              onChange={(event) => setOnlyFilled(event.currentTarget.checked)}
            />
            <Badge
              size="lg"
              variant="light"
              color={off ? 'statusWarning' : 'statusSuccess'}
              data-testid="gas-sum"
              aria-live="polite"
              title={off ? t('gasComposition.editor.sumOff', { diff: Math.abs(Math.round((sum - 100) * 1e6) / 1e6) }) : t('gasComposition.editor.sumOk')}
            >
              {t('gasComposition.editor.sumValue', { sum: Math.round(sum * 1e6) / 1e6 })}
            </Badge>
          </Group>
        </Group>
        <Text size="xs" c={off ? 'statusWarning' : 'dimmed'}>
          {off
            ? t('gasComposition.editor.sumOff', { diff: Math.abs(Math.round((sum - 100) * 1e6) / 1e6) })
            : t('gasComposition.editor.sumOk')}
        </Text>

        {grouped.map(({ group, items }) => (
          <Table key={group} layout={narrow === true ? 'auto' : 'fixed'} striped withTableBorder verticalSpacing={4} captionSide="top" data-testid={`gas-group-${group}`}>
            <Table.Caption ta="start" fw={600}>
              {GroupLabel[group]()}
            </Table.Caption>
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col">{t('gasComposition.editor.component')}</Table.Th>
                <Table.Th scope="col" ta="end" w={150}>
                  {t('gasComposition.editor.value')}
                </Table.Th>
                {previous !== undefined && (
                  <Table.Th scope="col" ta="end" w={170}>
                    {t('gasComposition.editor.previous')}
                  </Table.Th>
                )}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((component) => {
                const name = componentName(component);
                const label = name === undefined ? component.code : `${name} (${component.code})`;
                const text = values[component.code] ?? '';
                const current = parse(text);
                const before = previousValues.get(component.code);
                const changed =
                  previous !== undefined && (typeof current === 'string' ? Number(current) : 0) !== Number(before ?? 0);
                return (
                  <Table.Tr key={component.code} data-changed={changed ? '' : undefined}>
                    <Table.Th scope="row" fw={400}>
                      <Text size="sm" span>
                        {name ?? component.code}
                      </Text>
                      {name !== undefined && (
                        <Text size="xs" c="dimmed" span ml="xs" ff="monospace" data-testid="gas-component-code">
                          {component.code}
                        </Text>
                      )}
                      {component.formula !== null && component.formula !== undefined && component.formula !== component.code && (
                        <Text size="xs" c="dimmed" span ml="xs">
                          {component.formula}
                        </Text>
                      )}
                      {changed && (
                        <Badge size="xs" variant="light" ml="xs">
                          {t('gasComposition.editor.changed')}
                        </Badge>
                      )}
                    </Table.Th>
                    <Table.Td ta="end" w={150}>
                      <TextInput
                        size="xs"
                        inputMode="decimal"
                        aria-label={`${label}, ${t('gasComposition.editor.value')}`}
                        value={text}
                        readOnly={readOnly}
                        error={invalid.has(component.code) ? t('gasComposition.editor.invalidNumber') : undefined}
                        styles={{ input: { textAlign: 'end' } }}
                        onChange={(event) => {
                          const next = event.currentTarget.value;
                          setValues((all) => ({ ...all, [component.code]: next }));
                        }}
                      />
                    </Table.Td>
                    {previous !== undefined && (
                      <Table.Td ta="end" c="dimmed" w={170}>
                        {before ?? '—'}
                      </Table.Td>
                    )}
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        ))}

        {source?.flertAliases !== null && source?.flertAliases !== undefined && (
          <Text size="xs" c="dimmed">
            {t('gasComposition.editor.flertAliases', { values: source.flertAliases })}
          </Text>
        )}

        {!readOnly && off && (
          <Checkbox
            data-testid="gas-accept-deviation"
            checked={accept}
            onChange={(event) => setAccept(event.currentTarget.checked)}
            label={
              filled.length === 0
                ? t('gasComposition.editor.acceptNoComposition')
                : t('gasComposition.editor.acceptDeviation', { sum: Math.round(sum * 1e6) / 1e6 })
            }
          />
        )}

        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose}>
            {readOnly ? t('common.close') : t('common.cancel')}
          </Button>
          {!readOnly && (
            <Button
              disabled={!canSave}
              loading={saveLoading}
              onClick={() => {
                if (!canSave) return;
                save.mutate();
              }}
            >
              {t('gasComposition.editor.save')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
