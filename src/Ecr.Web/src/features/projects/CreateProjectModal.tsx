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

/**
 * Перелік поясів IANA.
 *
 * ⛔ `supportedValuesOf('timeZone')` віддає САМЕ ідентифікатори IANA
 * (`Asia/Aqtau`) — інших сервер не приймає (`ECR-CFG-4221`, директива ПК-1
 * №06 §3). Вільного введення тут немає навмисно: пояс вічний (`ФВ-1.1a`), і
 * опечатка в ньому стала б вічною властивістю проєкту.
 *
 * ⚠ `supportedValuesOf` є не всюди (і немає в старих середовищах). Без
 * запасного варіанта поле лишалося б порожнім, а проєкт — нествореним: сервер
 * відхиляє створення без поясу.
 */
function timeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const all = typeof supported === 'function' ? supported('timeZone') : [];

  const fallback = ['Asia/Almaty', 'Asia/Aqtau', 'Asia/Atyrau', 'Asia/Oral', 'Europe/London', 'UTC'];

  return all.length > 0 ? all : fallback;
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

    // ⚠ Часовий пояс — ПРОЄКТУ, а не сервера і не браузера: межі періоду
    // рахуються в ньому (`D-6`). Сервер у Європі не має вирішувати, коли
    // закінчився місяць на місці видобутку — і браузер конфігуратора теж.
    timeZoneId: form.timeZoneId,
    templateVersionId: Number(form.versionId),
    periodPolicyId: Number(form.policyId),
  };
}

/**
 * Чого формі бракує, щоб її можна було надіслати.
 *
 * ⛔ Винесене ОКРЕМОЮ чистою функцією з тієї ж причини, що й
 * {@link createProjectBody} (`D1-12`): рендер Mantine у jsdom для цієї форми
 * йде понад дві хвилини, тобто такий тест вимкнули б. Перевіряти ж тут є що —
 * пояс став обов'язковим, і саме забутий пункт у цьому переліку дозволив би
 * надіслати форму без нього.
 *
 * ⚠ `timeZoneId === null` означає «не обрано». Порожній рядок сюди не
 * потрапляє: `Select` віддає або значення зі списку, або `null`.
 */
export function createProjectIncomplete(form: {
  code: string;
  name: LocalizedValue;
  timeZoneId: string | null;
  versionId: string | null;
  policyId: string | null;
}): boolean {
  return (
    form.code.trim().length === 0 ||
    !hasAnyText(form.name) ||
    form.timeZoneId === null ||
    form.versionId === null ||
    form.policyId === null
  );
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

  // ⛔ Поле починається ПОРОЖНІМ (директива ПК-1 №06 §3 скасувала `D1-09`).
  // Тут стояв пояс браузера як початкове значення — і це та сама мовчазна
  // підстановка, тільки на крок пізніше: форму можна було надіслати, жодного
  // разу не глянувши на поле, і пояс конфігуратора ставав ВІЧНОЮ властивістю
  // проєкту (`ФВ-1.1a`). Конфігуратор сидить в Астані, майданчик — в Актау,
  // це +06:00 проти +05:00: кожен період закривався б на годину раніше, ніж
  // чекають на місці, і помітили б це за скаргою «не встиг подати».
  const [timeZoneId, setTimeZoneId] = useState<string | null>(null);
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
          // `timeZoneId` тут уже не `null`: кнопка недоступна, доки пояс не
          // обрано (`incomplete` нижче).
          createProjectBody({ code, name, periodKind, timeZoneId: timeZoneId ?? '', versionId, policyId }),
        ),
      }),
    onSuccess: async (result) => {
      setCode('');
      setName({});
      setTimeZoneId(null);
      setVersionId(null);
      setPolicyId(null);
      onClose();
      await onCreated(result.projectId);
    },
    onError: showApiError,
  });

  // ⛔ Пояс у переліку обов'язкових. Без нього форму можна було надіслати
  // (браузерне значення підставлялося саме), і сервер приймав її — з чужим
  // поясом, який після відкриття першого періоду вже не змінити (`ФВ-1.1a`).
  const incomplete = createProjectIncomplete({ code, name, timeZoneId, versionId, policyId });

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

      {/* ⛔ Пояс ВИДИМИЙ, ОБОВ'ЯЗКОВИЙ і без початкового значення (директива
          ПК-1 №06 §3). Спершу він надсилався мовчки з браузера, потім браузер
          лише підставляв початкове значення — обидва варіанти дозволяли
          створити проєкт, жодного разу не подивившись на поле. Помилку тут не
          виправити після відкриття першого періоду (`ФВ-1.1a`). */}
      <Select
        mt="sm"
        required
        searchable
        // ⚠ Поясів близько шестисот. Без межі список малюється весь: пошук
        // лишається по ВСЬОМУ переліку, обрізається лише показане.
        limit={50}
        label={t('periods.timeZone')}
        description={t('periods.timeZoneHint')}
        data={timeZones()}
        value={timeZoneId}
        onChange={setTimeZoneId}
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
