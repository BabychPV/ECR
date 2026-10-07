import { readFileSync, writeFileSync } from 'node:fs';
import { EcrClient } from './client.ts';
import { formatReport } from './compare.ts';
import { loadMapping, loadSettings } from './config.ts';
import { parseCsv } from './csv.ts';
import { prepareRows, runRegression } from './pipeline.ts';

const settings = loadSettings(process.argv.slice(2), process.env);
const mapping = loadMapping(settings.mappingPath);
const rows = prepareRows(parseCsv(readFileSync(settings.csvPath, 'utf8')), mapping);

const client = new EcrClient(settings.baseUrl);
await client.login(settings.userName, settings.password);
const { documentId, report } = await runRegression(client, mapping, rows);

console.log(formatReport(report));
// ⚠ У json — значення з еталону: файл лишається локально, у git не потрапляє.
writeFileSync(settings.outPath, JSON.stringify({ documentId, ...report }, null, 2));
console.log(`Документ ${documentId}; результат: ${settings.outPath}`);
process.exitCode = report.mismatches.length === 0 ? 0 : 1;
