#!/usr/bin/env node
// Verifies the participant distribution scope defined in ../README.md: the canonical docs/ tree.
// Keep this dependency-free so a moderator can run it before sending the materials.
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import vm from 'node:vm';

const repositoryRoot = fs.realpathSync(process.cwd());
const docsRoot = fs.realpathSync(path.join(repositoryRoot, 'docs'));
const prototypeRoot = fs.realpathSync(path.join(docsRoot, 'design/v2/validation/prototype'));
const storyboardSource = fs.readFileSync(path.join(prototypeRoot, 'storyboard.js'), 'utf8');
const errors = [];
const checked = [];
let generatedEdges = 0;

function auditTarget(sourceFile, rawTarget, edge) {
  const target = rawTarget.split('#')[0];
  if (!target) return;
  if (/^(?:https?:|mailto:|javascript:)/i.test(target)) {
    errors.push(`${edge}: external or executable target is outside the self-contained docs/ bundle: ${rawTarget}`);
    return;
  }
  if (/^data:/i.test(target)) return;
  if (path.isAbsolute(target)) {
    errors.push(`${edge}: absolute target is not portable: ${rawTarget}`);
    return;
  }
  const resolved = path.resolve(path.dirname(sourceFile), target);
  if (!fs.existsSync(resolved)) {
    errors.push(`${edge}: target does not exist: ${rawTarget}`);
    return;
  }
  const real = fs.realpathSync(resolved);
  const relative = path.relative(docsRoot, real);
  if (!relative || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    errors.push(`${edge}: target escapes canonical docs/ bundle: ${rawTarget}`);
    return;
  }
  checked.push(`${edge} -> ${path.relative(repositoryRoot, real)}`);
}

// Static participant entry pages own every file asset and the journey/research links. Attribute
// parsing accepts either quote style and includes link/script/image/media assets, not href alone.
for (const name of fs.readdirSync(prototypeRoot).filter((entry) => /\.html$/i.test(entry)).sort()) {
  const file = path.join(prototypeRoot, name);
  const source = fs.readFileSync(file, 'utf8');
  for (const match of source.matchAll(/\b(?:href|src)\s*=\s*(["'])(.*?)\1/gi)) {
    auditTarget(file, match[2], path.relative(repositoryRoot, file));
  }
}

// Generated Setup links are data, not literals in the rendered HTML. Load only content.js in a
// blank VM (it assigns one object and performs no I/O), then require storyboard.js to consume every
// manifest member. Resolve every edge for both variants because both independently render Setup.
const contentSandbox = { window: {} };
vm.runInNewContext(fs.readFileSync(path.join(prototypeRoot, 'content.js'), 'utf8'), contentSandbox,
  { filename: 'content.js', timeout: 1000 });
const dynamicLinks = contentSandbox.window.STORYBOARD_CONTENT?.distributionLinks;
if (!dynamicLinks || Object.keys(dynamicLinks).length === 0) {
  errors.push('content.js: distributionLinks manifest is missing or empty');
} else {
  for (const [name, target] of Object.entries(dynamicLinks)) {
    if (!storyboardSource.includes(`C.distributionLinks.${name}`)) {
      errors.push(`storyboard.js: generated participant link does not consume distributionLinks.${name}`);
      continue;
    }
    for (const variant of ['variant-a.html', 'variant-b.html']) {
      auditTarget(path.join(prototypeRoot, variant), String(target), `${variant} generated ${name}`);
      generatedEdges += 1;
    }
  }
}

// Every other generated link must remain a same-document #/ route. These guards make a newly added
// filesystem or network literal fail until it joins the audited manifest above.
if (!/function href\(path\) \{ return '#\/' \+ path; \}/.test(storyboardSource)) {
  errors.push('storyboard.js: href() no longer guarantees same-document #/ routes');
}
for (const match of storyboardSource.matchAll(/\bhref\s*=\s*["']([^"']+)["']/g)) {
  const literal = match[1];
  if (literal.startsWith('#') || literal.includes('C.distributionLinks')) continue;
  if (!literal.includes("' +") && !literal.includes('" +')) {
    errors.push(`storyboard.js: unaudited generated href literal: ${literal}`);
  }
}

if (errors.length) throw new Error(`Distribution-link audit failed:\n${errors.join('\n')}`);
console.log(`Distribution-link audit passed: ${checked.length} participant-facing file edges resolve inside canonical docs/ (${checked.length - generatedEdges} static, ${generatedEdges} generated manifest edges across both variants).`);
