import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { CreateDocumentRequest, DocumentIdResponse, PagedProjects } from '@/api/types';
import { groupRuleViolations } from './groupRuleViolations';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
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
 * ⛔ Версію шаблону визначає ПРОЄКТ, а не вибір людини (V-12, V-11). Тут
 * стояли `GET /templates` і `GET /template-versions/{id}/structure` — обидва
 * вимагають `Template.View`, і оператор із `Document.Create` бачив «You do not
 * have permission», хоча `POST /documents` від нього — 201. До того ж діалог
 * пропонував версії всіх шаблонів, а документ на версії, іншій за версію
 * проєкту, відкривався без аркушів. Тепер — `GET /projects/{id}/document-template`
 * (те саме право, що й на створення): версія проєкту, її аркуші й правила
 * складу.
 */

// ⚠ Прямо зі схеми: `api/types.ts` — спільний файл.
type DocumentTemplateDto = components['schemas']['DocumentTemplateDto'];
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
  const [sheets, setSheets] = useState<number[]>([]);

  // ⛔ Опційне: `BusinessKey` лишається унікальним технічним ключем
  // незалежно від того, чи задане ім'я (директива "людське ім'я документа").
  const [name, setName] = useState<LocalizedValue>({});

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
    enabled: opened,
  });

  const template = useQuery({
    queryKey: ['projects', Number(projectId), 'document-template'],
    queryFn: () =>
      apiFetch<DocumentTemplateDto>(`/api/v1/projects/${projectId ?? ''}/document-template`),
    enabled: opened && projectId !== null,
  });

  const versionId = template.data?.templateVersionId ?? null;

  const create = useMutation({
    mutationFn: () =>
      apiFetch<DocumentIdResponse>('/api/v1/documents', {
        method: 'POST',
        body: JSON.stringify({
          projectId: Number(projectId),
          templateVersionId: Number(versionId),
          sheetDefIds: sheets,

          // ⚠ Поле пропускається цілком, а не надсилається `undefined`:
          // порожній об'єкт і відсутність імені — те саме за змістом, і
          // `exactOptionalPropertyTypes` не дозволяє явний `undefined` на
          // опційному полі — лише його відсутність.
          ...(hasAnyText(name) ? { name } : {}),
        } satisfies CreateDocumentRequest),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['documents'] });

      onClose();
      setSheets([]);
      setName({});
      showDone(t('documents.created'));

      // Одразу відкриваємо документ: інакше користувач шукає його в переліку
      // за бізнес-ключем, якого ще не бачив.
      await navigate(`/documents/${result.documentId}`);
    },

    // ⚠ Порушення складу приходить переліком: «група Water вимагає всіх
    // аркушів, бракує W-02». Це те, що людина може виправити прямо тут.
    onError: showApiError,
  });

  const available = (template.data?.sheets ?? []).map((sheet) => ({
    id: sheet.id,
    label: `${localized(sheet.nameL10n) || sheet.code} (${sheet.code})`,
  }));

  // ⛔ Директива "live-попередження про порушення SheetGroupRule": ДО цього
  // порушення складу дізнавалися лише після відхиленого `POST /documents`.
  // Кнопка «Save» нижче НЕ блокується — сервер лишається останньою лінією
  // правди про всяк випадок, якщо ця копія колись розійдеться з оригіналом.
  const violations = groupRuleViolations(template.data, sheets);

  /*
   * ⛔ Перелік проєктів збирався через `?? []`, тобто відмова сервера робила
   * його порожнім і мовчала: людина читала його як «активних проєктів немає»
   * — і йшла заводити ще один (той самий дефект, що в `CreateProjectModal`).
   */
  const sourceError = projects.error ?? null;

  return (
    <Modal opened={opened} onClose={onClose} title={t('documents.create')} size="lg">
      {/* ⛔ Перед полями: причину видно ДО того, як людина почне гадати, чому
          переліки порожні. Решта діалогу лишається робочою. */}
      {sourceError !== null && <ErrorAlert error={sourceError} onRetry={() => void projects.refetch()} />}

      <Select
        label={t('documents.project')}
        placeholder={t('periods.pickProject')}
        data={(projects.data?.items ?? [])
          // ⛔ Лише активні: у чернетці періоди закриті, і документ у ній не
          // прийме жодного значення (`A7-25`).
          .filter((project) => project.status === 'Active')
          .map((project) => ({ value: String(project.id), label: project.code }))}
        value={projectId}
        onChange={(value) => {
          setProjectId(value);

          // Аркуші належать версії проєкту: залишений вибір від попереднього
          // послав би на сервер ідентифікатори з чужої структури.
          setSheets([]);
        }}
        data-autofocus
      />

      {/*
        ⚠ Відмова цього запиту — окремим банером: без нього «немає права» чи
        збій сервера виглядали б як проєкт без аркушів.
      */}
      {template.error !== null && (
        <ErrorAlert error={template.error} onRetry={() => void template.refetch()} />
      )}

      {/* ⚠ Лише показ: версію визначає проєкт, обирати її нема з чого. */}
      {template.data !== undefined && (
        <Text size="sm" mt="sm">
          {t('documents.version')}: {template.data.templateCode} · {template.data.version}
        </Text>
      )}

      <Stack gap="xs" mt="sm">
        <LocalizedInput
          label={t('documents.name')}
          description={t('documents.nameHint')}
          value={name}
          onChange={setName}
        />
      </Stack>

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
                onChange={(event) => {
                  // ⛔ `event.currentTarget` — поле СИНТЕТИЧНОЇ події, і React
                  // обнуляє його одразу після завершення цього обробника
                  // (`react.dev`: «After the event handler has been called,
                  // event.currentTarget will be set to null»). Функція-апдейтер
                  // `setSheets` читала його ЛІНИВО, у момент виклику React —
                  // під `StrictMode` (є в `main.tsx`) React навмисно викликає
                  // апдейтер ДВІЧІ, і на другому виклику `currentTarget` уже
                  // `null`: `TypeError: Cannot read properties of null (reading
                  // 'checked')`, і без `ErrorBoundary` на цьому маршруті — весь
                  // застосунок замінюється голим «Unexpected Application
                  // Error!» React Router. Тепер `checked` читається ОДРАЗУ,
                  // синхронно в обробнику, а не всередині апдейтера.
                  const checked = event.currentTarget.checked;

                  setSheets((current) =>
                    checked ? [...current, sheet.id] : current.filter((id) => id !== sheet.id),
                  );
                }}
              />
            ))}
          </Stack>

          {/* ⛔ Непорушний, не блокуючий «Save»: сервер — остання лінія
              правди (`ValidateCompositionAsync`), а тут — попередження ДО
              спроби зберегти. */}
          {violations.length > 0 && (
            <Stack gap="xs" mt="xs">
              {violations.map((message) => (
                <Text key={message} size="sm" c="statusError">
                  {message}
                </Text>
              ))}
            </Stack>
          )}
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
