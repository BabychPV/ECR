import { describe, expect, it } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { DataSourceFormModal } from '@/features/integration/DataSourceFormModal';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * Підказки діалогу «Нове з'єднання»: під кожним полем — що вводити, а в полі — приклад у форматі транспорту.
 * Зміст підказок і прикладів звірено з сервером (`DataSourceHandlers.ValidateAsync`,
 * `DataSourceEndpointPolicy`, адаптери); тут перевіряється, що вони є на екрані й перемикаються з транспортом.
 * Каталог у тестах віддає `⟦ключ⟧`, тому перевіряються ключі.
 *
 * ⛔ Поле, якого транспорт не читає (Sql — запасна адреса, PiSqlClient — каталог), приховане; уже введене чи
 * збережене значення — лише для читання з поясненням і кнопкою очищення (сервер такого значення не зберігає).
 * МУТАЦІЯ (доведено вручну): `ReadsSecondary.Sql = true` — червоні «Sql ховає запасну адресу» й «збережене
 * значення лише для читання».
 */

function openForm(source: DataSource | null = null): void {
  renderKeepMounted(<DataSourceFormModal opened source={source} onClose={() => undefined} />);
}

const hint = (key: string): HTMLElement => screen.getByText(`⟦${key}⟧`);

function pickTransport(name: string): void {
  fireEvent.click(screen.getByLabelText(/sources\.transport/));
  fireEvent.click(screen.getByRole('option', { name }));
}

const placeholderOf = (label: RegExp): string | null => screen.getByLabelText(label).getAttribute('placeholder');

const Stored: DataSource = {
  id: 3,
  code: 'FLERT',
  nameL10n: { en: 'FLERT' },
  transport: 'Sql',
  endpoint: 'Server=flert.example.local;Database=Vol;Integrated Security=true',
  secondaryEndpoint: 'Server=flert2.example.local;Database=Vol;Integrated Security=true',
  catalog: null,
  maxParallel: 4,
  isActive: true,
  hasSecret: false,
  rowVersion: 'AAAAAAAAB9E=',
  sourceEntityCount: 0,
  collectionScheduleCount: 0,
} as unknown as DataSource;

describe('Підказки полів нового з\'єднання', () => {
  it('кожне поле має опис, а технічні поля — приклад у полі (типово PI SQL Client)', () => {
    openForm();

    for (const key of [
      'sources.codeHint',
      'sources.nameHint',
      'sources.transportHint.PiSqlClient',
      'sources.endpointHint.PiSqlClient',
      'sources.secondaryEndpointHint.PiSqlClient',
      'sources.maxParallelHint',
    ]) {
      expect(hint(key)).toBeTruthy();
    }

    expect(placeholderOf(/sources\.code/)).toBe('PI_MAIN');
    expect(placeholderOf(/sources\.name/)).toBe('⟦sources.namePlaceholder⟧');
    expect(placeholderOf(/sources\.endpoint/)).toBe(
      'Driver={PI SQL Client};AF Server=pi.example.local;AF Database=ECR;Integrated Security=SSPI',
    );
    expect(placeholderOf(/sources\.secondaryEndpoint/)).toContain('pi02.example.local');
    expect(placeholderOf(/sources\.maxParallel/)).toBe('4');

    // PI SQL Client каталогу не читає — поля немає.
    expect(screen.queryByLabelText(/sources\.catalog/)).toBeNull();
  });

  it('опис і приклад адреси, запасної адреси та каталогу перемикаються разом із транспортом', () => {
    openForm();

    pickTransport('PiWebApi');
    expect(hint('sources.transportHint.PiWebApi')).toBeTruthy();
    expect(hint('sources.endpointHint.PiWebApi')).toBeTruthy();
    expect(hint('sources.secondaryEndpointHint.PiWebApi')).toBeTruthy();
    expect(hint('sources.catalogHint.PiWebApi')).toBeTruthy();
    expect(screen.queryByText('⟦sources.endpointHint.PiSqlClient⟧')).toBeNull();
    expect(placeholderOf(/sources\.endpoint/)).toBe('https://pi.example.local/piwebapi');
    expect(placeholderOf(/sources\.catalog/)).toBe('ECR');

    pickTransport('Sql');
    expect(hint('sources.transportHint.Sql')).toBeTruthy();
    expect(hint('sources.endpointHint.Sql')).toBeTruthy();
    expect(hint('sources.catalogHint.Sql')).toBeTruthy();
    expect(screen.queryByText('⟦sources.endpointHint.PiWebApi⟧')).toBeNull();
    expect(placeholderOf(/sources\.endpoint/)).toBe(
      'Server=sql01.example.local;Database=Reports;Integrated Security=true',
    );
    expect(placeholderOf(/sources\.catalog/)).toBe('Reports');
  });

  it('Sql ховає запасну адресу, PI SQL Client — каталог; PI Web API показує обидва', () => {
    openForm();

    pickTransport('Sql');
    expect(screen.queryByLabelText(/sources\.secondaryEndpoint/)).toBeNull();
    expect(screen.queryByText('⟦sources.secondaryEndpointHint.Sql⟧')).toBeNull();
    expect(screen.getByLabelText(/sources\.catalog/)).toBeTruthy();

    pickTransport('PiSqlClient');
    expect(screen.getByLabelText(/sources\.secondaryEndpoint/)).toBeTruthy();
    expect(screen.queryByLabelText(/sources\.catalog/)).toBeNull();

    pickTransport('PiWebApi');
    expect(screen.getByLabelText(/sources\.secondaryEndpoint/)).toBeTruthy();
    expect(screen.getByLabelText(/sources\.catalog/)).toBeTruthy();
  });

  it('введене значення не зникає мовчки: після зміни транспорту — лише для читання з поясненням і очищенням', () => {
    openForm();

    pickTransport('PiWebApi');
    fireEvent.change(screen.getByLabelText(/sources\.catalog/), { target: { value: 'ECR' } });

    pickTransport('PiSqlClient');
    const catalog = screen.getByLabelText(/sources\.catalog/) as HTMLInputElement;
    expect(catalog.value).toBe('ECR');
    expect(catalog.readOnly).toBe(true);
    expect(hint('sources.catalogHint.PiSqlClient')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /sources\.clearUnusedField/ }));
    expect(screen.queryByLabelText(/sources\.catalog/)).toBeNull();
  });

  it('збережена запасна адреса Sql — лише для читання з поясненням, не відкидається мовчки', () => {
    openForm(Stored);

    const secondary = screen.getByLabelText(/sources\.secondaryEndpoint/) as HTMLInputElement;
    expect(secondary.value).toBe(Stored.secondaryEndpoint);
    expect(secondary.readOnly).toBe(true);
    expect(hint('sources.secondaryEndpointHint.Sql')).toBeTruthy();
    expect(screen.getByRole('button', { name: /sources\.clearUnusedField/ })).toBeTruthy();
  });

  it('приклади не несуть облікових даних і справжніх хостів', () => {
    openForm();

    const examples: string[] = [];
    for (const name of ['PiSqlClient', 'PiWebApi', 'Sql']) {
      pickTransport(name);
      for (const label of [/sources\.endpoint/, /sources\.secondaryEndpoint/, /sources\.catalog/]) {
        const input = screen.queryByLabelText(label);
        examples.push(input?.getAttribute('placeholder') ?? '');
      }
    }

    for (const example of examples) {
      expect(example).not.toMatch(/password|pwd=|user id|uid=|secret|token|@/i);
      if (example.includes('.')) expect(example).toContain('.example.local');
    }
  });
});
