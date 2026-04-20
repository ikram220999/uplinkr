#!/usr/bin/env node
import { spawn } from 'child_process';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const tsxPath = join(__dirname, 'node_modules', '.bin', 'tsx');
const cliPath = join(__dirname, 'cli.jsx');
const args = process.argv.slice(2);

const child = spawn(tsxPath, [cliPath, ...args], { stdio: 'inherit', shell: true });

child.on('exit', (code) => {
  process.exit(code);
});
