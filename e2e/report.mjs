// Prints one line per suite and the grand total from e2e/results/*.json (written by each suite).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'results');
const files = fs.existsSync(dir) ? fs.readdirSync(dir).filter(f => f.endsWith('.json')).sort() : [];
let pass = 0, total = 0, crashed = 0;
for (const f of files) {
  const rs = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
  const ok = rs.filter(r => r.ok).length;
  pass += ok; total += rs.length;
  const complete = fs.existsSync(path.join(dir, f.replace('.json', '.done')));
  if (!complete) crashed++;
  console.log(`${!complete ? 'CRASH' : ok === rs.length ? 'PASS' : 'FAIL'}  ${f.replace('.json', '').padEnd(34)} ${ok}/${rs.length}${complete ? '' : '  (the suite stopped before its last check: see its output)'}`);
  for (const r of rs.filter(r => !r.ok)) console.log(`        FAILED ${r.id}: ${r.name}${r.detail ? ' (' + r.detail + ')' : ''}`);
}
console.log(`\nTOTAL ${pass}/${total} checks passed in ${files.length} suite(s)`);
process.exit(pass === total && total > 0 && crashed === 0 ? 0 : 1);
