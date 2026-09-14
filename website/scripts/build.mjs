import { cp, mkdir, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const outputRoot = path.join(websiteRoot, 'dist');
const entries = ['index.html', 'styles.css', 'main.js', 'scene.js', 'assets'];

async function validateSources() {
  const missing = [];

  for (const entry of entries) {
    try {
      const metadata = await stat(path.join(websiteRoot, entry));
      const expectedDirectory = entry === 'assets';
      if ((expectedDirectory && !metadata.isDirectory()) || (!expectedDirectory && !metadata.isFile())) {
        missing.push(`${entry} (${expectedDirectory ? 'directory' : 'file'} expected)`);
      }
    } catch {
      missing.push(entry);
    }
  }

  if (missing.length > 0) {
    throw new Error(`Build inputs are missing or invalid:\n- ${missing.join('\n- ')}`);
  }

  const unsafeAssets = [];
  async function inspectAssets(directory, relativeDirectory = '') {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const relativePath = path.join(relativeDirectory, entry.name);
      if (entry.name.startsWith('.') || entry.isSymbolicLink()) unsafeAssets.push(relativePath);
      else if (entry.isDirectory()) await inspectAssets(path.join(directory, entry.name), relativePath);
    }
  }

  await inspectAssets(path.join(websiteRoot, 'assets'));
  if (unsafeAssets.length > 0) {
    throw new Error(`Assets contain dotfiles or symbolic links:\n- ${unsafeAssets.join('\n- ')}`);
  }
}

await validateSources();
await mkdir(outputRoot, { recursive: true });

for (const entry of entries) {
  await cp(path.join(websiteRoot, entry), path.join(outputRoot, entry), {
    recursive: entry === 'assets',
    force: true,
    errorOnExist: false
  });
}

console.log(`Built ${entries.length} public entries in ${path.relative(process.cwd(), outputRoot) || 'dist'}.`);
