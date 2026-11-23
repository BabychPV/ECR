import { describe, expect, it } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { DataSourceFormModal } from '@/features/integration/DataSourceFormModal';
import { renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * Підказки діалогу «Нове з'єднання»: під кожним полем — що вводити, а в полі — приклад у форматі транспорту.
 * Зміст підказок і прикладів звірено з сервером (`DataSourceHandlers.ValidateAsync`,
 * `DataSourceEndpointPolicy`, адаптери); тут перевіряється, що вони є на екрані й перемикаються з транспортом.
 * Каталог у тестах віддає `⟦ключ⟧`, тому перевіряються ключі.
 */

function openForm(): void {
  renderKeepMounted(<DataSourceFormModal opened source={null} onClose={() => undefined} />);
}

const hint = (key: string): HTMLElement => screen.getByText(`⟦${key}⟧`);

function pickTransport(name: string): void {
  fireEvent.click(screen.getByLabelText(/sources\.transport/));
  fireEvent.click(screen.getByRole('option', { name }));
}

const placeholderOf = (label: RegExp): string | null => screen.getByLabelText(label).getAttribute('placeholder');

describe('Підказки полів нового з\'єднання', () => {
  it('кожне поле має опис, а технічні поля — приклад у полі (типово PI SQL Client)', () => {
    openForm();

    for (const key of [
      'sources.codeHint',
      'sources.nameHint',
      'sources.transportHint.PiSqlClient',
      'sources.endpointHint.PiSqlClient',
      'sources.secondaryEndpointHint.PiSqlClient',
      'sources.catalogHint.PiSqlClient',
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
    expect(hint('sources.secondaryEndpointHint.Sql')).toBeTruthy();
    expect(hint('sources.catalogHint.Sql')).toBeTruthy();
    expect(screen.queryByText('⟦sources.endpointHint.PiWebApi⟧')).toBeNull();
    expect(placeholderOf(/sources\.endpoint/)).toBe(
      'Server=sql01.example.local;Database=Reports;Integrated Security=true',
    );
    // Запасна адреса для Sql не використовується — прикладу немає.
    expect(placeholderOf(/sources\.secondaryEndpoint/)).toBeNull();
    expect(placeholderOf(/sources\.catalog/)).toBe('Reports');
  });

  it('приклади не несуть облікових даних і справжніх хостів', () => {
    openForm();

    const examples: string[] = [];
    for (const name of ['PiSqlClient', 'PiWebApi', 'Sql']) {
      pickTransport(name);
      examples.push(
        placeholderOf(/sources\.endpoint/) ?? '',
        placeholderOf(/sources\.secondaryEndpoint/) ?? '',
        placeholderOf(/sources\.catalog/) ?? '',
      );
    }

    for (const example of examples) {
      expect(example).not.toMatch(/password|pwd=|user id|uid=|secret|token|@/i);
      if (example.includes('.')) expect(example).toContain('.example.local');
    }
  });
});
