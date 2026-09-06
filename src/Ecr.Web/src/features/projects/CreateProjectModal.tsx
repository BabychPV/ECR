import { useState, type JSX } from 'react';
import { Button, Group, Modal, Select, TextInput } from '@mantine/core';
import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  CreateProjectRequest,
  PeriodPolicyDto,
  ProjectIdResponse,
  TemplatePage,
  TemplateVersionSummary,
} from '@/api/types';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Види періоду; значення збігаються з `PeriodKind` домену. */
const PeriodKinds = ['Monthly', 'Quarterly', 'Yearly', 'Custom'];

/** Пояс браузера — лише ПОЧАТКОВЕ значення поля, не рішення за користувача. */
const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

/**
 * Перелік поясів IANA.
 *
 * ⚠ `supportedValuesOf` є не всюди (і немає в старих середовищах). Без
 * запасного варіанта поле лишалося б порожнім, а проєкт — нествореним: сервер
 * відхиляє створення без поясу (`ECR-CFG-0422`, `D-5`).
 */
function timeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const all = typeof supported === 'function' ? supported('timeZone') : [];

  const fallback = ['Asia/Almaty', 'Asia/Aqtau', 'Asia/Atyrau', 'Asia/Oral', 'Europe/London', 'UTC'];
  const list = all.length > 0 ? all : fallback;

  // Пояс браузера має бути в переліку навіть тоді, коли середовище його не
  // перелічує: інакше попередньо обране значення виглядало б як помилка.
  return list.includes(browserZone) ? list : [browserZone, ...list];
}

/**
 * Тіло запиту на створення проєкту.
 *
 * ⛔ Винесене з компонента ОКРЕМОЮ функцією навмисно. Дефект `A7-56` жив
 * саме тут: у тілі не було ані `templateVersionId`, ані `periodPolicyId`, і
 * сервер відхиляв кожну спробу (`ECR-TMPL-0404`). Перевірити це рендером
 * Mantine у jsdom можна, але один такий тест іде понад дві хвилини — тобто
 * його вимкнуть. Чиста функція перевіряється за мілісекунди й пильнує рівно
 * те, що ламалося: **склад тіла**.
 *
 * ⚠ Числа перетворюються ТУТ. `Select` віддає рядок, а сервер чекає `int`:
 * `"42"` у полі `templateVersionId` дало б `400` ще до обробника.
 */
export function createProjectBody(form: {
  code: string;
  name: LocalizedValue;
  periodKind: string;
  timeZoneId: string;
  versionId: string | null;
  policyId: string | null;
}): CreateProjectRequest {
  return {
    code: form.code.trim(),
    nameL10n: form.name,
    periodKind: form.periodKind,

    // ⚠ Часовий пояс — ПРОЄКТУ, а не сервера: межі періоду рахуються в ньому
    // (`D-6`). Сервер у Європі не має вирішувати, коли закінчився місяць на
    // місці видобутку.
    timeZoneId: form.timeZoneId,
    templateVersionId: Number(form.versionId),
    periodPolicyId: Number(form.policyId),
  };
}

/**
 * Створення проєкту (`ФВ-1.1`, `D-5`).
 *
 * ⛔ Форма винесена зі сторінки не заради стислості. `A7-56`: вона надсилала
 * запит **без версії шаблону і без політики періодів**, а сервер відхиляє
 * створення без них (`ECR-TMPL-0404`, `ECR-PRD-0422`). Тобто перший крок
 * роботи із системою не працював із інтерфейсу жодного разу, а сторож «кожна
 * дія сервера має споживача» був зелений: споживач був, просто ніколи не
 * спрацьовував. Окремий компонент дає цій формі власний тест, який дивиться
 * на **тіло запиту**, а не на те, що форма відкрилася.
 *
 * ⚠ Проєкт створюється ЧЕРНЕТКОЮ: періоди лишаються закритими, доки його не
 * активують. Це не зайвий крок — календар будується з рішень, які доти ще
 * можна виправити без сліду в аудиті.
 */
export function CreateProjectModal({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (projectId: number) => void | Promise<void>;
}): JSX.Element {
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});
  const [periodKind, setPeriodKind] = useState('Monthly');

  // ⚠ Пояс браузера — ПОЧАТКОВЕ значення, а не рішення за користувача
  // (`D-5`). Конфігуратор часто сидить не там, де майданчик, і саме його
  // пояс мовчки ставав би вічною властивістю проєкту: після відкриття
  // першого періоду змінити його вже не можна (`ФВ-1.1a`).
  const [timeZoneId, setTimeZoneId] = useState(browserZone);
  const [versionId, setVersionId] = useState<string | null>(null);
  const [policyId, setPolicyId] = useState<string | null>(null);

  const templates = useQuery({
    queryKey: ['templates'],
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
    enabled: opened,
  });

  const versionQueries = useQueries({
    queries: (templates.data?.items ?? []).map((template) => ({
      queryKey: ['template-versions', template.id],
      queryFn: () =>
        apiFetch<TemplateVersionSummary[]>(`/api/v1/templates/${template.id}/versions`),
      enabled: opened,
    })),
  });

  const policies = useQuery({
    queryKey: ['period-policies'],
    queryFn: () => apiFetch<PeriodPolicyDto[]>('/api/v1/projects/period-policies'),
    enabled: opened,
  });

  /** Опубліковані версії всіх шаблонів, підписані кодом шаблону. */
  const publishedVersions = (templates.data?.items ?? []).flatMap((template, index) =>
    (versionQueries[index]?.data ?? [])
      .filter((version) => version.status === 'Published')
      .map((version) => ({
        value: String(version.id),
        label: `${template.code} · ${version.version}`,
      })),
  );

  const create = useMutation({
    mutationFn: () =>
      apiFetch<ProjectIdResponse>('/api/v1/projects', {
        method: 'POST',
        body: JSON.stringify(
          createProjectBody({ code, name, periodKind, timeZoneId, versionId, policyId }),
        ),
      }),
    onSuccess: async (result) => {
      setCode('');
      setName({});
      setVersionId(null);
      setPolicyId(null);
      onClose();
      await onCreated(result.projectId);
    },
    onError: showApiError,
  });

  const incomplete =
    code.trim().length === 0 || !hasAnyText(name) || versionId === null || policyId === null;

  return (
    <Modal opened={opened} onClose={onClose} title={t('periods.create')}>
      <TextInput
        label={t('periods.code')}
        description={t('periods.codeHint')}
        value={code}
        onChange={(event) => setCode(event.currentTarget.value)}
        data-autofocus
      />

      <LocalizedInput label={t('periods.name')} value={name} onChange={setName} />

      {/* ⛔ Вид періоду задається при створенні і потім визначає весь
          календар: у квартальному проєкті `Sequence` іде від 1 до 4, і
          змінити це згодом означало б переписати ключі всіх даних. */}
      <Select
        mt="sm"
        label={t('periods.kind')}
        description={t('periods.kindHint')}
        data={PeriodKinds}
        value={periodKind}
        onChange={(value) => setPeriodKind(value ?? 'Monthly')}
        allowDeselect={false}
      />

      {/* ⛔ Пояс ВИДИМИЙ і змінюваний (`D-5`). Раніше він надсилався мовчки з
          браузера конфігуратора, а помилку в ньому не виправити після
          відкриття першого періоду — побачити її треба тут. */}
      <Select
        mt="sm"
        searchable
        // ⚠ Поясів близько шестисот. Без межі список малюється весь: пошук
        // лишається по ВСЬОМУ переліку, обрізається лише показане.
        limit={50}
        label={t('periods.timeZone')}
        description={t('periods.timeZoneHint')}
        data={timeZones()}
        value={timeZoneId}
        onChange={(value) => setTimeZoneId(value ?? browserZone)}
        allowDeselect={false}
      />

      {/* ⚠ Лише ОПУБЛІКОВАНІ версії: чернетка не має ані замороженої
          структури, ані гарантії, що комірки знайдуть свої описи. */}
      <Select
        mt="sm"
        label={t('periods.templateVersion')}
        description={t('periods.templateVersionHint')}
        data={publishedVersions}
        value={versionId}
        onChange={setVersionId}
      />

      <Select
        mt="sm"
        label={t('periods.policy')}
        description={t('periods.policyHint')}
        data={(policies.data ?? []).map((policy) => ({
          value: String(policy.id),
          label: `${policy.code} · +${policy.graceOffsetDays}/${policy.hardCloseOffsetDays}`,
        }))}
        value={policyId}
        onChange={setPolicyId}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button disabled={incomplete} loading={create.isPending} onClick={() => create.mutate()}>
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
