import { useCallback, useEffect, useMemo, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { Anchor, Group, SegmentedControl, SimpleGrid, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, RegistryDefinitionDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { compositionOf } from './composition';
import { CompositionPanel, compositionKeys } from './CompositionPanel';
import { buildCompositionTree, type CompositionNode } from './compositionTree';
import { DateOnlyInput, formatDateOnly } from './DateOnlyInput';

/** Адреса редактора master-detail довідника. */
export function compositionEditorPath(code: string): string {
  return `/admin/registries/${encodeURIComponent(code)}/composition`;
}

function loadDefinition(code: string): Promise<RegistryDefinitionDto> {
  return apiFetch<RegistryDefinitionDto>(`/api/v1/registries/${encodeURIComponent(code)}/definition`);
}

/** Сьогодні `yyyy-MM-dd` за місцевим часом — типова бізнес-дата чинності. */
function today(): string {
  return formatDateOnly(new Date());
}

/** Обраний рядок панелі. */
interface Selection {
  readonly id: number;
  readonly label: string;
}

/**
 * Ланцюжок панелей нижче обраного рядка: на кожному рівні — одна з частин (перша, якщо їх
 * кілька, або обрана перемикачем), і так до листка чи до рядка без вибору.
 */
function DetailChain({
  node,
  parent,
  depth,
  asOf,
  readOnly,
  registries,
  onDirty,
  lockedAbove,
}: {
  readonly node: CompositionNode;
  readonly parent: Selection;
  readonly depth: number;
  readonly asOf: string;
  readonly readOnly: boolean;
  readonly registries: readonly RegistryDefDto[];
  readonly onDirty: (code: string, count: number) => void;
  readonly lockedAbove: (depth: number) => boolean;
}): JSX.Element | null {
  const [childCode, setChildCode] = useState<string | null>(null);
  const [selected, setSelected] = useState<Selection | null>(null);

  const child = node.children.find((item) => item.definition.code === childCode) ?? node.children[0];
  if (child === undefined) return null;

  return (
    <Stack gap="md">
      {node.children.length > 1 && (
        <SegmentedControl
          size="xs"
          aria-label={t('registries.rc816.chooseParts')}
          value={child.definition.code}
          data={node.children.map((item) => ({
            value: item.definition.code,
            label: localized(item.definition.nameL10n) || item.definition.code,
          }))}
          onChange={(value) => {
            setChildCode(value);
            setSelected(null);
          }}
        />
      )}

      <CompositionPanel
        key={`${child.definition.code}:${parent.id}`}
        node={child}
        parentId={parent.id}
        parentLabel={parent.label}
        asOf={asOf}
        readOnly={readOnly}
        registries={registries}
        selectedId={child.children.length > 0 ? selected?.id ?? null : undefined}
        onSelect={child.children.length > 0 ? (id, label) => setSelected({ id, label }) : undefined}
        selectionLocked={lockedAbove(depth + 2)}
        onDirty={onDirty}
      />

      {selected !== null && (
        <DetailChain
          node={child}
          parent={selected}
          depth={depth + 1}
          asOf={asOf}
          readOnly={readOnly}
          registries={registries}
          onDirty={onDirty}
          lockedAbove={lockedAbove}
        />
      )}
    </Stack>
  );
}

/**
 * Редактор master-detail довідника (`ФВ-8.16`, `D-155`, `D-157`; FEATURE-REGISTRY-TABLES §8.4,
 * крок RT-32): довідник і ланцюжок його частин (`STREAM → STREAM_CASE → GAS_COMPOSITION`)
 * редагуються як одна таблиця — обрав рядок угорі, правиш його склад праворуч; поле композиції
 * заповнюється батьком саме.
 *
 * ⛔ Окрема сторінка й лінивий чанк: редактор не потрапляє ні в бандл переліку довідників, ні в
 * бюджет `DocumentPage`.
 *
 * ⚠ Незбережені зміни частин блокують вибір іншого батька: інакше перемикання мовчки викинуло б
 * склад, який людина ще не зберегла. Вийти зі сторінки не дає `UnsavedGuard` через реєстр джерел.
 */
export function CompositionEditorPage(): JSX.Element {
  const { code = '' } = useParams();
  const session = useSession();
  const [asOf, setAsOf] = useState(today);
  const [selected, setSelected] = useState<Selection | null>(null);
  const [dirtyByCode, setDirtyByCode] = useState<Readonly<Record<string, number>>>({});
  const rootRef = useRef<HTMLDivElement>(null);

  const definition = useQuery({
    queryKey: queryKeys.registries.definition(code),
    queryFn: () => loadDefinition(code),
    refetchOnWindowFocus: false,
  });

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });

  const tree = useQuery({
    queryKey: compositionKeys.tree(code),
    queryFn: () => buildCompositionTree(definition.data as RegistryDefinitionDto, registries.data ?? [], loadDefinition),
    enabled: definition.data !== undefined && registries.data !== undefined,
    refetchOnWindowFocus: false,
  });

  const onDirty = useCallback((panel: string, count: number) => {
    setDirtyByCode((all) => (all[panel] === count ? all : { ...all, [panel]: count }));
  }, []);

  const totalDirty = Object.values(dirtyByCode).reduce((sum, count) => sum + count, 0);
  const totalRef = useRef(totalDirty);
  totalRef.current = totalDirty;

  useEffect(
    () =>
      registerUnsavedSource('registry-composition', {
        hasUnsaved: () => totalRef.current > 0,
        unsavedCount: () => totalRef.current,
      }),
    [],
  );

  /** Рівні, глибші за `depth`, мають незбережені зміни — вибір на рівні `depth − 1` заблоковано. */
  const levelsDirty = useMemo(() => {
    const depthOf = new Map<string, number>();
    function visit(node: CompositionNode, depth: number): void {
      depthOf.set(node.definition.code, depth);
      node.children.forEach((child) => visit(child, depth + 1));
    }
    if (tree.data !== undefined) visit(tree.data, 0);
    return (depth: number) =>
      Object.entries(dirtyByCode).some(([panel, count]) => count > 0 && (depthOf.get(panel) ?? 0) >= depth);
  }, [tree.data, dirtyByCode]);

  const mayEdit = can(session.data, 'Registry.EditData');

  /** `F6` — до наступної панелі, `Shift+F6` — до попередньої (§8.8, як у сітці документа). */
  function cyclePanels(event: KeyboardEvent<HTMLDivElement>): void {
    if (event.key !== 'F6' || rootRef.current === null) return;
    const panels = [...rootRef.current.querySelectorAll<HTMLElement>('[data-rc816-panel]')];
    if (panels.length === 0) return;
    event.preventDefault();
    const current = panels.findIndex((panel) => panel.contains(document.activeElement));
    const step = event.shiftKey ? -1 : 1;
    const next = panels[(current + step + panels.length) % panels.length];
    next?.focus();
  }

  return (
    <>
      <PageHeader title={t('registries.rc816.title')} />

      <AsyncBoundary<RegistryDefinitionDto>
        isPending={definition.isPending}
        error={definition.error}
        data={definition.data}
        onRetry={() => void definition.refetch()}
      >
        {(loaded) => {
          const parentLink = compositionOf(loaded);
          return (
            <div ref={rootRef} onKeyDown={cyclePanels}>
              <Stack gap="md">
                <Group gap="xs" wrap="wrap" align="end">
                  <Text fw={600}>{localized(loaded.nameL10n) || loaded.code}</Text>
                  <Text size="xs" c="dimmed">
                    {loaded.code}
                  </Text>
                  <Anchor component={Link} to={`/admin/registries/${encodeURIComponent(loaded.code)}/definition`} size="sm">
                    {t('registries.rc816.openDefinition')}
                  </Anchor>
                  <DateOnlyInput
                    label={t('registries.rc816.asOf')}
                    value={asOf}
                    disabled={totalDirty > 0}
                    onChange={(value) => {
                      if (value !== '') setAsOf(value);
                    }}
                  />
                </Group>

                <Text size="xs" c="dimmed">
                  {t('registries.rc816.hint')}
                </Text>

                {!mayEdit && <Banner tone="info" text={t('registries.rc816.readOnly')} />}

                {parentLink !== null && parentLink.parentRegistryCode !== null && (
                  <Banner
                    tone="info"
                    text={
                      <>
                        {t('registries.rc816.isPartOf', { parent: parentLink.parentRegistryCode })}{' '}
                        <Anchor component={Link} to={compositionEditorPath(parentLink.parentRegistryCode)}>
                          {t('registries.rc816.openParent')}
                        </Anchor>
                      </>
                    }
                  />
                )}

                {registries.error !== null && (
                  <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />
                )}
                {tree.error !== null && <ErrorAlert error={tree.error} onRetry={() => void tree.refetch()} />}

                {tree.data !== undefined && tree.data.children.length === 0 && (
                  <Text size="sm" c="dimmed" data-rc816-state="no-parts">
                    {t('registries.rc816.noChain')}
                  </Text>
                )}

                {tree.data !== undefined && (
                  <SimpleGrid cols={{ base: 1, lg: tree.data.children.length > 0 ? 2 : 1 }} spacing="lg">
                    <CompositionPanel
                      key={`${loaded.code}:${asOf}`}
                      node={tree.data}
                      parentId={null}
                      parentLabel={null}
                      asOf={asOf}
                      readOnly={!mayEdit}
                      registries={registries.data ?? []}
                      selectedId={tree.data.children.length > 0 ? selected?.id ?? null : undefined}
                      onSelect={
                        tree.data.children.length > 0 ? (id, label) => setSelected({ id, label }) : undefined
                      }
                      selectionLocked={levelsDirty(1)}
                      onDirty={onDirty}
                    />

                    {tree.data.children.length > 0 &&
                      (selected === null ? (
                        <Text size="sm" c="dimmed" data-rc816-state="choose-parent">
                          {t('registries.rc816.chooseParent')}
                        </Text>
                      ) : (
                        <DetailChain
                          key={asOf}
                          node={tree.data}
                          parent={selected}
                          depth={0}
                          asOf={asOf}
                          readOnly={!mayEdit}
                          registries={registries.data ?? []}
                          onDirty={onDirty}
                          lockedAbove={levelsDirty}
                        />
                      ))}
                  </SimpleGrid>
                )}

                {totalDirty > 0 && (
                  <Text size="xs" c="dimmed" role="status">
                    {t('registries.rc816.unsavedTotal', { count: totalDirty })}
                  </Text>
                )}
              </Stack>
            </div>
          );
        }}
      </AsyncBoundary>
    </>
  );
}
