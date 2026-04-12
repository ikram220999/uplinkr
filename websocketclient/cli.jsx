#!/usr/bin/env tsx

import React from 'react';
import { render } from 'ink';
import App from './app.jsx';

const args = process.argv.slice(2);

if (args.length < 2 || args[0] !== 'http') {
  console.log(`
Usage: proxiee http <port>

Examples:
  proxiee http 5000
  proxiee http 8080
  `);
  process.exit(1);
}

const port = args[1];

if (isNaN(parseInt(port, 10))) {
  console.error(`Error: Port must be a number`);
  process.exit(1);
}

const { waitUntilExit } = render(<App port={port} />);

waitUntilExit().catch((err) => {
  console.error(err);
  process.exit(1);
});
