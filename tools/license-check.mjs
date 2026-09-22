#!/usr/bin/env node
// Перевірка ліцензій залежностей — блокуючий крок CI (D-12, D-82).
//
// Використання:
//   node tools/license-check.mjs nuget      — після `dotnet restore Ecr.sln`
//   node tools/license-check.mjs npm        — після `npm ci` у src/Ecr.Web
//   node tools/license-check.mjs self-test  — самоперевірка на фікстурах
//
// ⚠ Без жодної залежності: NuGet читається з obj/project.assets.json (повний
// граф разом із транзитивними) + .nuspec у кеші пакетів; npm — з
// package-lock.json + node_modules/*/package.json. Сторонній інструмент тут
// сам став би залежністю, ліцензію якої теж треба перевіряти.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

// ── SPDX-вираз: OR — достатньо однієї дозволеної гілки, AND — усі.
function parseSpdx(expr) {
  const tokens = expr.replace(/[()]/g, (m) => ` ${m} `).trim().split(/\s+/).filter(Boolean);
  let i = 0;
  const primary = () => {
    if (tokens[i] === '(') { i++; const n = orExpr(); i++; return n; }
    let id = tokens[i++];
    if (tokens[i] === 'WITH') { i += 2; }
    return { id };
  };
  const andExpr = () => {
    const parts = [primary()];
    while (/^and$/i.test(tokens[i] ?? '')) { i++; parts.push(primary()); }
    return parts.length === 1 ? parts[0] : { and: parts };
  };
  const orExpr = () => {
    const parts = [andExpr()];
    while (/^or$/i.test(tokens[i] ?? '')) { i++; parts.push(andExpr()); }
    return parts.length === 1 ? parts[0] : { or: parts };
  };
  return orExpr();
}

const isDenied = (id, policy) => policy.denied.some((p) => new RegExp(p, 'i').test(id));

function allowedBy(node, policy) {
  if (node.or) return node.or.some((n) => allowedBy(n, policy));
  if (node.and) return node.and.every((n) => allowedBy(n, policy));
  // ⛔ Заборона сильніша за дозвіл: помилково вписаний у `allowed` копілефт
  // однаково відхиляється.
  if (isDenied(node.id, policy)) return false;
  return policy.allowed.includes(node.id);
}

function versionAtLeast(v, floor) {
  const a = v.split(/[.-]/).map(Number), b = floor.split('.').map(Number);
  for (let k = 0; k < b.length; k++) {
    if ((a[k] || 0) !== b[k]) return (a[k] || 0) > b[k];
  }
  return true;
}

export function evaluate(packages, policy) {
  const violations = [];
  for (const p of packages) {
    const banned = policy.forbiddenPackages.find((f) =>
      f.ecosystem === p.ecosystem && f.name.toLowerCase() === p.name.toLowerCase() &&
      (!f.fromVersion || versionAtLeast(p.version, f.fromVersion)));
    if (banned) { violations.push({ ...p, reason: `заборонений пакет: ${banned.reason}` }); continue; }

    const exc = policy.exceptions.find((e) =>
      e.ecosystem === p.ecosystem && e.name.toLowerCase() === p.name.toLowerCase() &&
      (!e.version || e.version === p.version));
    if (exc) continue;

    if (!p.license) { violations.push({ ...p, reason: 'ліцензію не визначено' }); continue; }
    if (!allowedBy(parseSpdx(p.license), policy)) {
      violations.push({ ...p, reason: 'ліцензія не в переліку дозволених' });
    }
  }
  return violations;
}

// ── NuGet: граф із project.assets.json, ліцензія з .nuspec.
function nugetLicense(nuspecPath, policy) {
  if (!fs.existsSync(nuspecPath)) return null;
  const xml = fs.readFileSync(nuspecPath, 'utf8');
  const expr = xml.match(/<license\s+type="expression"\s*>([^<]+)<\/license>/i);
  if (expr) return expr[1].trim();
  const url = xml.match(/<licenseUrl>([^<]+)<\/licenseUrl>/i);
  if (url) return policy.licenseUrls[url[1].trim()] ?? null;
  return null;
}

export function collectNuget(repoRoot, policy, packagesDir) {
  const found = new Map();
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (e.isDirectory()) {
        if (['node_modules', '.git', 'bin'].includes(e.name)) continue;
        walk(path.join(dir, e.name));
      } else if (e.name === 'project.assets.json' && path.basename(dir) === 'obj') {
        const assets = JSON.parse(fs.readFileSync(path.join(dir, e.name), 'utf8'));
        const cache = packagesDir ?? assets.project?.restore?.packagesPath;
        for (const [key, lib] of Object.entries(assets.libraries ?? {})) {
          if (lib.type !== 'package') continue;
          const [name, version] = key.split('/');
          const k = `${name}@${version}`;
          if (found.has(k)) continue;
          const nuspec = path.join(cache, lib.path, `${name.toLowerCase()}.nuspec`);
          found.set(k, { ecosystem: 'nuget', name, version, license: nugetLicense(nuspec, policy) });
        }
      }
    }
  };
  for (const d of ['src', 'tests', 'installer']) {
    if (fs.existsSync(path.join(repoRoot, d))) walk(path.join(repoRoot, d));
  }
  return [...found.values()];
}

// ── npm: перелік із lock-файла, ліцензія з node_modules (або з lock, якщо
// платформний опційний пакет не встановлено).
export function collectNpm(webDir) {
  const lock = JSON.parse(fs.readFileSync(path.join(webDir, 'package-lock.json'), 'utf8'));
  const out = [];
  for (const [p, meta] of Object.entries(lock.packages ?? {})) {
    if (!p || meta.link) continue;
    const pj = path.join(webDir, p, 'package.json');
    let license = meta.license ?? null, name = p.split('node_modules/').pop(), version = meta.version;
    if (fs.existsSync(pj)) {
      const j = JSON.parse(fs.readFileSync(pj, 'utf8'));
      name = j.name ?? name; version = j.version ?? version;
      license = typeof j.license === 'string' ? j.license : (j.license?.type ?? license);
      if (!license && Array.isArray(j.licenses)) license = j.licenses.map((l) => l.type ?? l).join(' OR ');
    }
    out.push({ ecosystem: 'npm', name, version, license, dev: !!meta.dev });
  }
  return out;
}

function loadPolicy() {
  return JSON.parse(fs.readFileSync(path.join(root, 'contracts', 'license-policy.json'), 'utf8'));
}

function report(violations, total, label) {
  if (violations.length === 0) {
    console.log(`ліцензії ${label}: ${total} пакетів, порушень немає`);
    return 0;
  }
  console.error(`ліцензії ${label}: ${violations.length} порушень із ${total}:`);
  for (const v of violations) {
    console.error(`  ${v.name} ${v.version}  [${v.license ?? '—'}]  ${v.reason}${v.dev ? ' (dev)' : ''}`);
  }
  console.error('Виправлення: замінити пакет або внести виняток із причиною в contracts/license-policy.json.');
  return 1;
}

// ── Самоперевірка: GPL → відмова (навіть якщо GPL помилково в allowed),
// MIT → ок, без ліцензії → відмова, виняток → ок, заборонений пакет → відмова.
function selfTest() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ecr-lic-'));
  try {
    const cache = path.join(tmp, 'cache');
    const mk = (id, ver, licXml) => {
      const d = path.join(cache, id.toLowerCase(), ver);
      fs.mkdirSync(d, { recursive: true });
      fs.writeFileSync(path.join(d, `${id.toLowerCase()}.nuspec`), `<package><metadata>${licXml}</metadata></package>`);
    };
    mk('Good.Mit', '1.0.0', '<license type="expression">MIT</license>');
    mk('Bad.Gpl', '2.0.0', '<license type="expression">GPL-3.0-only</license>');
    mk('No.License', '1.0.0', '');
    mk('Dual', '1.0.0', '<license type="expression">GPL-2.0-only OR MIT</license>');
    mk('FluentAssertions', '8.0.0', '<license type="expression">MIT</license>');
    const obj = path.join(tmp, 'src', 'P', 'obj');
    fs.mkdirSync(obj, { recursive: true });
    const lib = (id, v) => [`${id}/${v}`, { type: 'package', path: `${id.toLowerCase()}/${v}` }];
    fs.writeFileSync(path.join(obj, 'project.assets.json'), JSON.stringify({
      libraries: Object.fromEntries([lib('Good.Mit', '1.0.0'), lib('Bad.Gpl', '2.0.0'),
        lib('No.License', '1.0.0'), lib('Dual', '1.0.0'), lib('FluentAssertions', '8.0.0')]),
    }));
    const policy = {
      allowed: ['MIT', 'GPL-3.0-only'], // навмисна «помилка редактора»
      denied: ['^A?GPL', '^LGPL', '^SSPL'],
      licenseUrls: {}, exceptions: [],
      forbiddenPackages: [{ ecosystem: 'nuget', name: 'FluentAssertions', fromVersion: '8', reason: 'комерційна' }],
    };
    const v = evaluate(collectNuget(tmp, policy, cache), policy).map((x) => x.name).sort();
    const expect = ['Bad.Gpl', 'FluentAssertions', 'No.License'];
    const ok1 = JSON.stringify(v) === JSON.stringify(expect);

    policy.exceptions.push({ ecosystem: 'nuget', name: 'No.License', reason: 'тест' });
    const v2 = evaluate(collectNuget(tmp, policy, cache), policy).map((x) => x.name).sort();
    const ok2 = JSON.stringify(v2) === JSON.stringify(['Bad.Gpl', 'FluentAssertions']);

    const web = path.join(tmp, 'web');
    fs.mkdirSync(path.join(web, 'node_modules', 'gpl-pkg'), { recursive: true });
    fs.mkdirSync(path.join(web, 'node_modules', 'mit-pkg'), { recursive: true });
    fs.writeFileSync(path.join(web, 'node_modules', 'gpl-pkg', 'package.json'), '{"name":"gpl-pkg","version":"1.0.0","license":"AGPL-3.0"}');
    fs.writeFileSync(path.join(web, 'node_modules', 'mit-pkg', 'package.json'), '{"name":"mit-pkg","version":"1.0.0","license":"MIT"}');
    fs.writeFileSync(path.join(web, 'package-lock.json'), JSON.stringify({ packages: {
      '': {}, 'node_modules/gpl-pkg': { version: '1.0.0' }, 'node_modules/mit-pkg': { version: '1.0.0' } } }));
    const v3 = evaluate(collectNpm(web), policy).map((x) => x.name);
    const ok3 = JSON.stringify(v3) === JSON.stringify(['gpl-pkg']);

    if (ok1 && ok2 && ok3) { console.log('self-test: ок'); return 0; }
    console.error('self-test: ПРОВАЛ', { v, v2, v3 });
    return 1;
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

const mode = process.argv[2];
let code;
if (mode === 'self-test') {
  code = selfTest();
} else if (mode === 'nuget' || mode === 'npm') {
  code = selfTest();
  if (code === 0) {
    const policy = loadPolicy();
    const pkgs = mode === 'nuget'
      ? collectNuget(root, policy, process.env.NUGET_PACKAGES)
      : collectNpm(path.join(root, 'src', 'Ecr.Web'));
    if (pkgs.length === 0) { console.error(`ліцензії ${mode}: не знайдено жодного пакета — спершу restore / npm ci`); code = 1; }
    else code = report(evaluate(pkgs, policy), pkgs.length, mode);
  }
} else {
  console.error('використання: node tools/license-check.mjs nuget|npm|self-test');
  code = 2;
}
process.exit(code);
