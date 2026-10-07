import { useState, type JSX } from 'react';
import { Anchor, Button, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateVersionPage, TemplateVersionSummary } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { NewTemplateVersionModal } from './NewTemplateVersionModal';
import './templateVersionsTimeline.css';

/** Адреса сторінки структури версії — та сама, що й у переліку шаблонів. */
function templateVersionHref(templateId: number, versionId: number): string {
  return `/admin/templates/${String(templateId)}/versions/${String(versionId)}`;
}

/**
 * Версії шаблону на його картці (`U-19`).
 *
 * ⛔ Сторінка шаблону знала МЕНШЕ, ніж рядок переліку, з якого на неї
 * прийшли: у переліку — версії з посиланнями і «New version», на картці —
 * лише код і число залежних. Єдиний шлях до редактора структури був назад
 * через перелік.
 *
 * ⚠ Запит — ТОЙ САМИЙ, що в `TemplatesPage` (ключ `versionsOf`, адреса,
 * `?limit=100`): перехід із переліку на картку бере версії з кешу, а
 * створення версії звідси оновлює обидва екрани однією інвалідацією.
 *
 * ⛔ «New version» — лише з `editable`, і це та сама перевірка
 * `can(session, 'Template.Edit')`, що й у переліку; вирішує її сторінка, а не
 * цей компонент, щоб правило жило в одному місці на екран.
 *
 * ⚠ Кнопка `variant="default"`: головна дія картки — «Rename template» у
 * шапці, а `L1` дозволяє на екрані рівно одну `filled`.
 */
/**
 * Версії шаблону (до 100) — один запит на картку: його читають і стрічка версій,
 * і шапка картки (бейдж стану, «Continue draft», b4b).
 */
export function useTemplateVersions(templateId: number) {
  return useQuery({
    queryKey: queryKeys.templates.versionsOf(templateId),
    queryFn: () =>
      apiFetch<TemplateVersionPage>(`/api/v1/templates/${String(templateId)}/versions?limit=100`),
  });
}

export function TemplateVersionsSection({
  templateId,
  editable,
  newVersionIsPrimary = false,
}: {
  templateId: number;
  editable: boolean;
  /**
   * b4b (макет: «New draft from vX» — головна дія картки, коли чернетки немає):
   * тоді «New version» — заповнена кнопка; коли чернетка є, головна —
   * «Continue draft» у шапці, а ця лишається другорядною (одна filled, L1).
   */
  newVersionIsPrimary?: boolean;
}): JSX.Element {
  const [creating, setCreating] = useState(false);

  const versions = useTemplateVersions(templateId);

  const items = versions.data?.items;

  /** Остання версія — від неї клонується наступна (правило `TemplatesPage`). */
  const latest = items === undefined || items.length === 0 ? null : (items[items.length - 1]?.id ?? null);

  /*
   * ✎ b4b (макет `screens-templates.js`, `timeline`): стрічка версій від новішої до
   * старішої замість таблиці. «Поточна» — остання опублікована: з неї створюються
   * нові документи. Опису зміни й автора контракт не віддає (D15-06) — їх немає.
   */
  const newestFirst = items === undefined ? undefined : [...items].reverse();
  const current = newestFirst?.find((version) => version.status === 'Published')?.id ?? null;
  return (
    <Stack gap="xs" data-template-versions>
      <Group justify="space-between">
        {/* ⚠ h2 під h1 картки шаблону (axe `heading-order`, прохід a11y batch-4); вигляд h4. */}
        <Title order={2} size="h4">{t('templates.versions')}</Title>

        {editable && (
          <Button variant={newVersionIsPrimary ? 'filled' : 'default'} onClick={() => setCreating(true)}>
            {t('templates.newVersion')}
          </Button>
        )}
      </Group>

      <AsyncBoundary<TemplateVersionSummary[]>
        isPending={versions.isPending}
        error={versions.error}
        data={newestFirst}
        isEmpty={(rows) => rows.length === 0}
        emptyTitle={t('templates.versionsEmpty')}
        emptyHint={t('templates.versionsEmptyHint')}
        onRetry={() => void versions.refetch()}
      >
        {(rows) => (
          <ol className="tpl-tl" data-template-timeline="">
            {rows.map((version) => {
              const kind =
                version.id === current ? 'current' : version.status === 'Draft' ? 'draft' : 'old';

              return (
                <li key={version.id} data-version-kind={kind}>
                  <span className="tpl-tl-pt" aria-hidden="true">
                    <span className="tpl-tl-dot" data-kind={kind} />
                  </span>
                  <Stack gap="xs" miw={0}>
                    <Group gap="xs" wrap="wrap">
                      <Anchor
                        component={Link}
                        to={templateVersionHref(templateId, version.id)}
                        className="tpl-tl-version"
                        {...(kind === 'old' ? { c: 'dimmed' } : {})}
                      >
                        {version.version}
                      </Anchor>
                      <StatusBadge kind="version" state={version.status} quiet />
                      {kind === 'current' && (
                        <Text size="xs" c="dimmed">
                          {t('templates.currentVersion')}
                        </Text>
                      )}
                      {/* ⚠ Лічильник правок вигляду — лише коли він щось каже (див. TemplatesPage). */}
                      {version.presentationRevision > 0 && (
                        <Text size="xs" c="dimmed">
                          {t('version.presentationRevision', { revision: version.presentationRevision })}
                        </Text>
                      )}
                    </Group>
                    {version.publishedAt !== null && (
                      <Text size="xs" c="dimmed">
                        {t('templates.versionPublishedAt')} <Timestamp value={version.publishedAt} />
                      </Text>
                    )}
                  </Stack>
                </li>
              );
            })}
          </ol>
        )}
      </AsyncBoundary>
      <NewTemplateVersionModal
        templateId={creating ? templateId : null}
        cloneFrom={latest}
        onClose={() => setCreating(false)}
      />
    </Stack>
  );
}
