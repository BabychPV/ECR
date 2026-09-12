import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CreateDocumentRequest,
  DocumentIdResponse,
  PagedProjects,
  TemplatePage,
  TemplateStructureDto,
  TemplateVersionPage,
} from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Створення документа (`ФВ-3.1`, `ФВ-3.2`).
 *
 * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /documents` досяжним, бо
 * клієнт ЧИТАЄ `GET /documents` тією самою адресою (`A7-42`). Тобто система,
 * уся суть якої — заповнення документів, не мала способу створити перший.
 *
 * ⛔ Склад аркушів обирається ЯВНО і перевіряється сервером за
 * `SheetGroupRule` (`ФВ-3.2`): `RequiresAll` вимагає всіх аркушів групи,
 * `RequiresOne` — рівно одного. Тому тут немає «створити з усіма» — вибір
 * робить людина, а правило складу підтверджує або відхиляє його з поясненням.
 *
 * ⚠ Версія шаблону береться **опублікована**: чернетка не має ані
 * замороженої структури, ані гарантії, що комірки знайдуть свої описи.
 */
export function CreateDocumentModal({
  opened,
  onClose,
}: {
  opened: boolean;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const [projectId, setProjectId] = useState<string | null>(null);
  const [versionId, setVersionId] = useState<string | null>(null);
  const [sheets, setSheets] = useState<number[]>([]);

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
    enabled: opened,
  });

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: opened,
  });

  const templateItems = templates.data?.items ?? [];

  // ⚠ Версії читаються по кожному шаблону: маршрут контракту —
  // `GET /templates/{id}/versions`, окремого «усі версії» немає і не треба.
  //
  // ⛔ Q-275: той самий ендпоінт — курсорний (Q-225), відповідь
  // `{items, nextCursor, totalCount}`, а НЕ голий масив. Тут стояв тип
  // `TemplateVersionSummary[]`, тож `.data` при пагінованій відповіді був
  // ОБ'ЄКТОМ, не `undefined` — `?? []` не рятував, і `.filter` нижче падав
  // на кожному відкритті «Новий документ» (той самий дефект, що Q-274 в
  // `CreateProjectModal.tsx`). Взірець — `TemplatesPage.tsx`/`CreateProjectModal.tsx`:
  // `TemplateVersionPage`, `?limit=100`, `.data?.items`.
  const versionQueries = useQueries({
    queries: templateItems.map((template) => ({
      queryKey: queryKeys.templates.versionsOf(template.id),
      queryFn: () =>
        apiFetch<TemplateVersionPage>(`/api/v1/templates/${template.id}/versions?limit=100`),
      enabled: opened,
    })),
  });

  /** Опубліковані версії всіх шаблонів, підписані кодом шаблону. */
  const publishedVersions = templateItems.flatMap((template, index) =>
    (versionQueries[index]?.data?.items ?? [])
      .filter((version) => version.status === 'Published')
      .map((version) => ({
        value: String(version.id),
        label: `${template.code} · ${version.version}`,
      })),
  );

  const structure = useQuery({
    queryKey: queryKeys.templates.version(Number(versionId)),
    queryFn: () =>
      apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${versionId ?? ''}/structure`),
    enabled: opened && versionId !== null,
  });

  const create = useMutation({
    mutationFn: () =>
      apiFetch<DocumentIdResponse>('/api/v1/documents', {
        method: 'POST',
        body: JSON.stringify({
          projectId: Number(projectId),
          templateVersionId: Number(versionId),
          sheetDefIds: sheets,
        } satisfies CreateDocumentRequest),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['documents'] });

      onClose();
      setSheets([]);
      showDone(t('documents.created'));

      // Одразу відкриваємо документ: інакше користувач шукає його в переліку
      // за бізнес-ключем, якого ще не бачив.
      await navigate(`/documents/${result.documentId}`);
    },

    // ⚠ Порушення складу приходить переліком: «група Water вимагає всіх
    // аркушів, бракує W-02». Це те, що людина може виправити прямо тут.
    onError: showApiError,
  });

  const available = (structure.data?.sheets ?? []).map((sheet) => ({
    id: sheet.id,
    label: `${localized(sheet.nameL10n) || sheet.code} (${sheet.code})`,
  }));

  return (
    <Modal opened={opened} onClose={onClose} title={t('documents.create')} size="lg">
      <Select
        label={t('documents.project')}
        placeholder={t('periods.pickProject')}
        data={(projects.data?.items ?? [])
          // ⛔ Лише активні: у чернетці періоди закриті, і документ у ній не
          // прийме жодного значення (`A7-25`).
          .filter((project) => project.status === 'Active')
          .map((project) => ({ value: String(project.id), label: project.code }))}
        value={projectId}
        onChange={setProjectId}
        data-autofocus
      />

      <Select
        mt="sm"
        label={t('documents.version')}
        description={t('documents.versionHint')}
        placeholder={t('documents.pickVersion')}
        data={publishedVersions}
        value={versionId}
        onChange={(value) => {
          setVersionId(value);

          // Аркуші належать конкретній версії: залишений вибір від попередньої
          // послав би на сервер ідентифікатори з чужої структури.
          setSheets([]);
        }}
      />

      {versionId !== null && (
        <>
          <Text size="sm" mt="sm" fw={600}>
            {t('documents.sheets')}
          </Text>
          <Text size="xs" c="dimmed" mb="xs">
            {t('documents.sheetsHint')}
          </Text>

          <Stack gap="xs">
            {available.map((sheet) => (
              <Checkbox
                key={sheet.id}
                label={sheet.label}
                checked={sheets.includes(sheet.id)}
                onChange={(event) =>
                  setSheets((current) =>
                    event.currentTarget.checked
                      ? [...current, sheet.id]
                      : current.filter((id) => id !== sheet.id),
                  )
                }
              />
            ))}
          </Stack>
        </>
      )}

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={projectId === null || versionId === null || sheets.length === 0}
          loading={create.isPending}
          onClick={() => create.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
