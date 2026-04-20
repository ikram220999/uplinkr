import React from 'react';
import { render } from 'ink';
import App from './app.jsx';

const args = process.argv.slice(2).filter(a => a !== '--');

// Parse --log <path> option
let logDir = null;
const filteredArgs = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--log') {
    if (i + 1 >= args.length) {
      console.error('Error: --log requires a path argument');
      process.exit(1);
    }
    logDir = args[++i];
  } else {
    filteredArgs.push(args[i]);
  }
}

if (filteredArgs.length < 2 || filteredArgs[0] !== 'http') {
  console.log(`
Usage: uplinkr http <port> [--log <path>]

Examples:
  uplinkr http 5000
  uplinkr http 8080
  uplinkr http 3000 --log ./logs
  `);
  process.exit(1);
}

const port = filteredArgs[1];

if (isNaN(parseInt(port, 10))) {
  console.error(`Error: Port must be a number`);
  process.exit(1);
}

const { waitUntilExit } = render(<App port={port} logDir={logDir} />);

waitUntilExit().catch((err) => {
  console.error(err);
  process.exit(1);
});
