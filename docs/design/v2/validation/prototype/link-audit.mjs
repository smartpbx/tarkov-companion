#!/usr/bin/env node
// Verifies the participant distribution scope defined in ../README.md: a complete docs/ tree.
// Keep this small and dependency-free so a moderator can run it before sending the materials.
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const repositoryRoot = process.cwd();
const docsRoot = path.join(repositoryRoot, 'docs');
const prototypeRoot = path.join(docsRoot, 'design/v2/validation/prototype');
if (!fs.existsSync(prototypeRoot)) throw new Error('Run from the repository root; prototype distribution scope was not found.');

// Dynamic `#/route` links are constructed by storyboard.js and are same-document routes. Audit
// the static HTML distribution edges here; node --check separately covers the script syntax.
const files = fs.readdirSync(prototypeRoot).filter((name) => /\.html$/i.test(name));
const errors = [];
let references = 0;
for (const name of files) {
  const file = path.join(prototypeRoot, name);
  const source = fs.readFileSync(file, 'utf8');
  for (const match of source.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
    const target = match[1].split('#')[0];
    if (!target || /^(?:https?:|mailto:|data:)/.test(target)) continue;
    references += 1;
    const resolved = path.resolve(path.dirname(file), target);
    if (!resolved.startsWith(docsRoot + path.sep) || !fs.existsSync(resolved)) errors.push(`${path.relative(repositoryRoot, file)} -> ${target}`);
  }
}
if (errors.length) throw new Error(`Distribution-link audit failed:\n${errors.join('\n')}`);
console.log(`Distribution-link audit passed: ${references} local prototype references resolve inside docs/.`);
