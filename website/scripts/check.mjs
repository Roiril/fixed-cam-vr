import { execFile } from 'node:child_process';
import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const coreFiles = ['index.html', 'styles.css', 'main.js', 'scene.js', 'server.mjs'];
const syntaxFiles = ['main.js', 'scene.js', 'server.mjs', 'scripts/build.mjs', 'scripts/check.mjs'];
const errors = [];

async function isFile(relativePath) {
  try {
    return (await stat(path.join(websiteRoot, relativePath))).isFile();
  } catch {
    return false;
  }
}

for (const relativePath of coreFiles) {
  if (!(await isFile(relativePath))) errors.push(`Missing required file: ${relativePath}`);
}

const html = (await isFile('index.html')) ? await readFile(path.join(websiteRoot, 'index.html'), 'utf8') : '';
const ids = [...html.matchAll(/\bid\s*=\s*["']([^"']+)["']/gi)].map((match) => match[1]);
const duplicateIds = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))];
for (const id of duplicateIds) errors.push(`Duplicate HTML id: ${id}`);

const scriptSources = [...html.matchAll(/<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["'][^>]*>/gi)].map((match) => match[1]);
const duplicateScripts = [...new Set(scriptSources.filter((source, index) => scriptSources.indexOf(source) !== index))];
for (const source of duplicateScripts) errors.push(`Duplicate script source: ${source}`);

for (const match of html.matchAll(/<canvas\b([^>]*)>/gi)) {
  if (!/\bid\s*=\s*["'][^"']+["']/i.test(match[1])) errors.push('Canvas element is missing an id');
}

const references = [...html.matchAll(/\b(?:href|src)\s*=\s*["']([^"']+)["']/gi)].map((match) => match[1]);
const localFiles = new Set();

for (const reference of references) {
  if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(reference)) continue;

  const [rawPath, fragment = ''] = reference.split('#', 2);
  if (!rawPath) {
    if (fragment && !ids.includes(fragment)) errors.push(`Missing fragment target: #${fragment}`);
    continue;
  }

  let decodedPath;
  try {
    decodedPath = decodeURIComponent(rawPath.split('?', 1)[0]).replaceAll('\\', '/');
  } catch {
    errors.push(`Malformed local URL: ${reference}`);
    continue;
  }

  const segments = decodedPath.split('/').filter(Boolean);
  if (segments.some((segment) => segment === '..')) {
    errors.push(`Local URL escapes website root: ${reference}`);
    continue;
  }

  const relativePath = segments.join('/');
  if (!relativePath) continue;
  if (!(await isFile(relativePath))) errors.push(`Missing local reference: ${reference}`);
  else localFiles.add(relativePath);
}

for (const relativePath of syntaxFiles) {
  if (!(await isFile(relativePath))) continue;
  try {
    await execFileAsync(process.execPath, ['--check', path.join(websiteRoot, relativePath)]);
  } catch (error) {
    errors.push(`JavaScript syntax error in ${relativePath}: ${error.stderr?.trim() || error.message}`);
  }
}

let referencedBytes = 0;
for (const relativePath of localFiles) referencedBytes += (await stat(path.join(websiteRoot, relativePath))).size;

let assetCount = 0;
async function countAssets(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) await countAssets(entryPath);
    else if (entry.isFile()) assetCount += 1;
  }
}

try {
  await countAssets(path.join(websiteRoot, 'assets'));
} catch {
  errors.push('Missing required directory: assets');
}

if (errors.length > 0) {
  console.error(`Check failed (${errors.length}):\n- ${errors.join('\n- ')}`);
  process.exitCode = 1;
} else {
  console.log(`Check passed: ${coreFiles.length} required files, ${localFiles.size} local references (${referencedBytes} bytes), ${assetCount} assets, ${ids.length} unique ids.`);
}
