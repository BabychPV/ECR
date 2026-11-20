import { useMemo, useState, type JSX } from 'react';
import { Badge, Card, Group, List, SegmentedControl, Stack, Switch, Text, Title } from '@mantine/core';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';
import {
  useDependencyGraph,
  useSourceBindings,
  useSwitchBinding,
  type BindingChange,
  type SourceBinding,
} from '@/features/dependencyGraph/api';
import {
  buildGraphView,
  type FillsLink,
  type GraphView,
  type ReadLink,
  type TemplateView,
} from '@/features/dependencyGraph/graphView';

/** Місяці 1…12 з бітової маски обсягу ребра (`PeriodScope.MonthMask`). */
export function monthsOfMask(mask: number): string {
  const months: number[] = [];

  for (let month = 1; month <= 12; month += 1) {
    if ((mask & (1 << month)) !== 0) months.push(month);
  }

  return months.join(', ');
}

/** Підпис обсягу ребра за періодом. */
function scopeLabel(link: ReadLink['link']): string {
  if (link.scope === 'month') return t('dependencyGraph.scope.month');
  if (link.scope === 'year') {
    return link.monthMask === 0
      ? t('dependencyGraph.scope.year')
      : t('dependencyGraph.scope.months', { months: monthsOfMask(link.monthMask) });
  }

  return t('dependencyGraph.scope.all');
}

/** Підпис джерела доказу (відомий — за каталогом; невідомий — як прийшов, це службове слово сервера). */
function evidenceSourceLabel(source: string): string {
  switch (source) {
    case 'formulaDependency':
      return t('dependencyGraph.evidence.formulaDependency');
    case 'ast':
      return t('dependencyGraph.evidence.ast');
    case 'lookupColumn':
      return t('dependencyGraph.evidence.lookupColumn');
    default:
      return source;
  }
}

/** Підпис зауваження побудови (відоме — за каталогом; невідоме — службове слово сервера). */
function diagnosticLabel(key: string): string {
  switch (key) {
    case 'cycle':
      return t('dependencyGraph.diagnostic.cycle');
    case 'astFallback':
      return t('dependencyGraph.diagnostic.astFallback');
    case 'parseFailed':
      return t('dependencyGraph.diagnostic.parseFailed');
    case 'unknownRegistry':
      return t('dependencyGraph.diagnostic.unknownRegistry');
    default:
      return key;
  }
}

/** Зміна, яку людина просить підтвердити. */
interface PendingChange {
  readonly change: BindingChange;
  readonly binding: SourceBinding;
  readonly consequences: readonly string[];
  readonly danger: boolean;
}

function describeChange(change: BindingChange, binding: SourceBinding): PendingChange {
  if (change.kind === 'state') {
    const toApproved = change.sourceState === 'approvedOnly';

    return {
      change,
      binding,
      danger: false,
      consequences: [t(toApproved ? 'dependencyGraph.confirm.approvedOnly' : 'dependencyGraph.confirm.any')],
    };
  }

  if (change.readFromDocument !== undefined) {
    return {
      change,
      binding,
      danger: !change.readFromDocument,
      consequences: [t(change.readFromDocument ? 'dependencyGraph.confirm.readOn' : 'dependencyGraph.confirm.readOff')],
    };
  }

  const on = change.recalculateDependents === true;

  return {
    change,
    binding,
    danger: !on,
    consequences: [t(on ? 'dependencyGraph.confirm.recalcOn' : 'dependencyGraph.confirm.recalcOff')],
  };
}

interface ControlsProps {
  readonly binding: SourceBinding;
  readonly busy: boolean;
  readonly onRequest: (pending: PendingChange) => void;
}

/** Перемикачі зв'язки джерела: стан-ворота, «читати з документа», «перераховувати залежних». */
function BindingControls({ binding, busy, onRequest }: ControlsProps): JSX.Element {
  const label = binding.code;

  return (
    <Stack gap="xs" data-testid={`binding-controls-${String(binding.id)}`}>
      <SegmentedControl
        size="xs"
        aria-label={t('dependencyGraph.state.label', { code: label })}
        value={binding.sourceState}
        disabled={busy}
        data={[
          { value: 'any', label: t('dependencyGraph.state.any') },
          { value: 'approvedOnly', label: t('dependencyGraph.state.approvedOnly') },
        ]}
        onChange={(value) => {
          if (value === binding.sourceState) return;

          onRequest(
            describeChange(
              { kind: 'state', bindingId: binding.id, sourceState: value === 'approvedOnly' ? 'approvedOnly' : 'any' },
              binding,
            ),
          );
        }}
      />
      <Switch
        size="sm"
        label={t('dependencyGraph.flag.readFromDocument')}
        description={t('dependencyGraph.flag.readFromDocument.hint')}
        checked={binding.readFromDocument}
        disabled={busy}
        onChange={(event) =>
          onRequest(
            describeChange({ kind: 'flags', bindingId: binding.id, readFromDocument: event.currentTarget.checked }, binding),
          )
        }
      />
      <Switch
        size="sm"
        label={t('dependencyGraph.flag.recalculateDependents')}
        description={t('dependencyGraph.flag.recalculateDependents.hint')}
        checked={binding.recalculateDependents}
        disabled={busy}
        onChange={(event) =>
          onRequest(
            describeChange(
              { kind: 'flags', bindingId: binding.id, recalculateDependents: event.currentTarget.checked },
              binding,
            ),
          )
        }
      />
    </Stack>
  );
}

function FillsBlock({
  fills,
  bindings,
  busy,
  onRequest,
}: {
  readonly fills: readonly FillsLink[];
  readonly bindings: ReadonlyMap<number, SourceBinding>;
  readonly busy: boolean;
  readonly onRequest: (pending: PendingChange) => void;
}): JSX.Element | null {
  if (fills.length === 0) return null;

  return (
    <Stack gap="xs">
      <Text size="sm" fw={600}>
        {t('dependencyGraph.fills')}
      </Text>
      <List listStyleType="none" spacing="sm" withPadding={false}>
        {fills.map((fill) => {
          const binding = bindings.get(fill.bindingId);

          return (
            <List.Item key={fill.bindingId} data-testid={`fills-${String(fill.bindingId)}`}>
              <Stack gap="xs">
                <Text size="sm">
                  {t('dependencyGraph.fills.registry', { registry: fill.registryCode })}
                  {binding === undefined ? '' : ` · ${binding.code}`}
                </Text>
                <Text size="xs" c="dimmed">
                  {fill.consumerCodes.length === 0
                    ? t('dependencyGraph.fills.noConsumers')
                    : t('dependencyGraph.fills.consumers', { consumers: fill.consumerCodes.join(', ') })}
                </Text>
                {binding === undefined ? null : <BindingControls binding={binding} busy={busy} onRequest={onRequest} />}
              </Stack>
            </List.Item>
          );
        })}
      </List>
    </Stack>
  );
}

function ReadsBlock({ reads }: { readonly reads: readonly ReadLink[] }): JSX.Element | null {
  if (reads.length === 0) return null;

  return (
    <Stack gap="xs">
      <Text size="sm" fw={600}>
        {t('dependencyGraph.reads')}
      </Text>
      <List listStyleType="none" spacing="sm" withPadding={false}>
        {reads.map((read) => (
          <List.Item key={`${String(read.link.bindingId)}-${String(read.link.sourceTemplateId)}`}>
            <Group gap="xs" wrap="wrap">
              <Text size="sm">
                {t('dependencyGraph.reads.from', { source: read.sourceCode, registry: read.registryCode })}
              </Text>
              <Badge variant="light" color="gray" size="sm">
                {scopeLabel(read.link)}
              </Badge>
              {read.link.viaTemplateFormula ? (
                <Badge variant="outline" color="gray" size="sm">
                  {t('dependencyGraph.via.formula')}
                </Badge>
              ) : null}
              {read.link.viaMethodology ? (
                <Badge variant="outline" color="gray" size="sm">
                  {t('dependencyGraph.via.methodology')}
                </Badge>
              ) : null}
            </Group>
            <details>
              <summary>{t('dependencyGraph.evidence.summary')}</summary>
              <Stack gap="xs" mt="xs">
                {read.formulaEvidence.length > 0 ? (
                  <List size="xs" spacing="xs">
                    {read.formulaEvidence.map((evidence) => (
                      <List.Item key={`${evidence.source}-${String(evidence.id)}-${evidence.detail ?? ''}`}>
                        {evidenceSourceLabel(evidence.source)}
                        {evidence.detail === null || evidence.detail === undefined ? '' : ` · ${evidence.detail}`}
                      </List.Item>
                    ))}
                  </List>
                ) : null}
                {read.formulaTotal > read.formulaEvidence.length ? (
                  <Text size="xs" c="dimmed">
                    {t('dependencyGraph.evidence.more', { count: read.formulaTotal - read.formulaEvidence.length })}
                  </Text>
                ) : null}
                {read.methodologies.length > 0 ? (
                  <List size="xs" spacing="xs">
                    {read.methodologies.map((methodology) => (
                      <List.Item key={methodology.code}>
                        {methodology.direct
                          ? t('dependencyGraph.evidence.methodologyDirect', {
                              code: methodology.code,
                              formulas: methodology.formulas.join(', '),
                            })
                          : t('dependencyGraph.evidence.methodologyResult', {
                              code: methodology.code,
                              via: methodology.via ?? '',
                            })}
                      </List.Item>
                    ))}
                  </List>
                ) : null}
                {read.formulaEvidence.length === 0 && read.methodologies.length === 0 ? (
                  <Text size="xs" c="dimmed">
                    {t('dependencyGraph.evidence.none')}
                  </Text>
                ) : null}
              </Stack>
            </details>
          </List.Item>
        ))}
      </List>
    </Stack>
  );
}

function TemplateCard({
  template,
  bindings,
  busy,
  onRequest,
}: {
  readonly template: TemplateView;
  readonly bindings: ReadonlyMap<number, SourceBinding>;
  readonly busy: boolean;
  readonly onRequest: (pending: PendingChange) => void;
}): JSX.Element {
  return (
    <Card withBorder padding="md" data-testid={`template-${template.code}`}>
      <Stack gap="sm">
        <Group gap="xs">
          <Title order={3} size="h4">
            {template.code}
          </Title>
          {template.onCycle ? (
            <Badge color="statusWarning" variant="light">
              {t('dependencyGraph.onCycle')}
            </Badge>
          ) : null}
        </Group>
        <FillsBlock fills={template.fills} bindings={bindings} busy={busy} onRequest={onRequest} />
        <ReadsBlock reads={template.reads} />
        {template.fills.length === 0 && template.reads.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('dependencyGraph.noLinks')}
          </Text>
        ) : null}
      </Stack>
    </Card>
  );
}

function GraphBody({
  view,
  bindings,
}: {
  readonly view: GraphView;
  readonly bindings: ReadonlyMap<number, SourceBinding>;
}): JSX.Element {
  const toggle = useSwitchBinding();
  const [pending, setPending] = useState<PendingChange | null>(null);

  const confirm = (): void => {
    if (pending === null) return;

    toggle.mutate(pending.change, {
      onSuccess: () => {
        setPending(null);
        showDone(t('dependencyGraph.saved'));
      },
      onError: (error) => {
        setPending(null);
        showApiError(error);
      },
    });
  };

  return (
    <Stack gap="lg">
      {view.cycles.length > 0 ? (
        <Banner
          tone="warning"
          testId="dependency-graph-cycles"
          title={t('dependencyGraph.cycles.title')}
          text={
            <>
              <Text size="sm">{t('dependencyGraph.cycles.text')}</Text>
              <List size="sm" mt="xs">
                {view.cycles.map((cycle) => (
                  <List.Item key={cycle.codes.join('|')}>{cycle.codes.join(' ⇄ ')}</List.Item>
                ))}
              </List>
            </>
          }
        />
      ) : null}

      {view.truncated ? (
        <Banner tone="info" testId="dependency-graph-truncated" title={t('dependencyGraph.truncated')} />
      ) : null}

      {view.diagnostics.length > 0 ? (
        <section aria-labelledby="dependency-graph-diagnostics">
          <Title order={2} size="h3" id="dependency-graph-diagnostics">
            {t('dependencyGraph.diagnostics')}
          </Title>
          <List size="sm" mt="xs" data-testid="dependency-graph-diagnostics">
            {view.diagnostics.map((diagnostic) => (
              <List.Item key={`${diagnostic.key}-${diagnostic.detail}`}>
                {diagnosticLabel(diagnostic.key)}
                {` · ${diagnostic.detail}`}
              </List.Item>
            ))}
          </List>
        </section>
      ) : null}

      {view.levels.map((level) => (
        <section key={level.level} aria-labelledby={`dependency-level-${String(level.level)}`}>
          <Title order={2} size="h3" id={`dependency-level-${String(level.level)}`}>
            {t('dependencyGraph.level', { level: level.level + 1 })}
          </Title>
          <Text size="sm" c="dimmed" mb="sm">
            {level.level === 0 ? t('dependencyGraph.level.first') : t('dependencyGraph.level.next')}
          </Text>
          <List listStyleType="none" spacing="sm" withPadding={false}>
            {level.templates.map((template) => (
              <List.Item key={template.id}>
                <TemplateCard template={template} bindings={bindings} busy={toggle.isPending} onRequest={setPending} />
              </List.Item>
            ))}
          </List>
        </section>
      ))}

      <ConfirmModal
        opened={pending !== null}
        title={t('dependencyGraph.confirm.title', { code: pending?.binding.code ?? '' })}
        text={t('dependencyGraph.confirm.text')}
        consequences={pending?.consequences ?? []}
        verb={t('dependencyGraph.confirm.verb')}
        danger={pending?.danger ?? false}
        isPending={toggle.isPending}
        onConfirm={confirm}
        onClose={() => {
          if (!toggle.isPending) setPending(null);
        }}
      />
    </Stack>
  );
}

/**
 * «Граф залежностей» (`B179 L5`): які документи читають дані яких документів-джерел.
 *
 * ⚠ Граф будує сервер із того, що вже пише публікація (формули шаблонів і методології); екран нічого не обчислює, лише
 * показує рівні порядку обчислення, докази ребер, цикли й зауваження — і дає перемикачі зв'язок джерел (`any` /
 * `approvedOnly`, «читати з документа», «перераховувати залежних»). Кожне перемикання — через підтвердження з наслідками.
 */
export function DependencyGraphPage(): JSX.Element {
  const graph = useDependencyGraph();
  const bindings = useSourceBindings();

  const view = useMemo(() => (graph.data === undefined ? undefined : buildGraphView(graph.data)), [graph.data]);
  const byId = useMemo(() => new Map((bindings.data ?? []).map((b) => [b.id, b] as const)), [bindings.data]);

  return (
    <>
      <PageHeader
        title={t('dependencyGraph.title')}
        {...(view === undefined
          ? {}
          : { meta: t('dependencyGraph.summary', { templates: view.templateCount, links: view.linkCount }) })}
      />
      <AsyncBoundary
        isPending={graph.isPending || bindings.isPending}
        error={graph.error ?? bindings.error}
        data={view}
        skeleton="table"
        isEmpty={(data) => data.templateCount === 0}
        emptyTitle={t('dependencyGraph.empty.title')}
        emptyHint={t('dependencyGraph.empty.hint')}
        emptyHeadingOrder={2}
        onRetry={() => {
          void graph.refetch();
          void bindings.refetch();
        }}
      >
        {(data) => <GraphBody view={data} bindings={byId} />}
      </AsyncBoundary>
    </>
  );
}
