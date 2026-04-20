import React, { useState, useEffect, useRef } from 'react';
import { Box, Text } from 'ink';
import { BridgeClient, generateTunnelId } from './bridge.js';
import { WS_SERVER_URL, BASE_DOMAIN } from './config.js';
import fs from 'fs';
import path from 'path';

function createFileLogger(logDir) {
  if (!logDir) return null;

  // Ensure the directory exists
  fs.mkdirSync(logDir, { recursive: true });

  const dateStr = new Date().toISOString().slice(0, 10); // YYYY-MM-DD
  const logFile = path.join(logDir, `${dateStr}-log.txt`);

  return function writeLog(line) {
    const timestamp = new Date().toISOString();
    fs.appendFileSync(logFile, `[${timestamp}] ${line}\n`, 'utf8');
  };
}

const App = ({ port, logDir }) => {
  const [status, setStatus] = useState('connecting'); // connecting | online | error
  const [tunnelInfo, setTunnelInfo] = useState(null);
  const [errorMsg, setErrorMsg] = useState('');
  const [logs, setLogs] = useState([]);
  const fileLog = useRef(createFileLogger(logDir));

  const addLog = (logStr) => {
    setLogs((prev) => {
      const next = [...prev, logStr];
      if (next.length > 5) return next.slice(next.length - 5);
      return next;
    });
  };

  useEffect(() => {
    let bridge = null;

    const init = async () => {
      try {
        const subdomain = generateTunnelId();
        const publicHost = BASE_DOMAIN ? `${subdomain}.${BASE_DOMAIN}` : null;
        const data = { subdomain, publicHost };

        setTunnelInfo(data);
        setStatus('connecting');

        bridge = new BridgeClient(WS_SERVER_URL, `http://localhost:${port}`, {
          onConnect: () => setStatus('online'),
          onClose: () => {
            if (status !== 'error') setStatus('connecting');
          },
          onRequest: ({ method, path }) => {
            if (fileLog.current) {
              fileLog.current(`REQUEST  ${method} ${path}`);
            }
          },
          onResponse: ({ method, path, status, durationMs }) => {
            const timeStr = new Date().toLocaleTimeString();
            const color = status >= 500 ? 'red' : status >= 400 ? 'yellow' : 'green';
            addLog(`[${timeStr}] ${method} ${path} -> ${status} (${durationMs}ms)`);
            if (fileLog.current) {
              fileLog.current(`RESPONSE ${method} ${path} -> ${status} (${durationMs}ms)`);
            }
          },
          onError: (msg) => {
            // We only log the error but don't crash, the bridge auto-reconnects
            addLog(`[ERROR] ${msg}`);
            if (fileLog.current) {
              fileLog.current(`ERROR    ${msg}`);
            }
          },

        }, data.subdomain);

        bridge.runForever();
      } catch (err) {
        setStatus('error');
        if (err.message.includes('fetch failed')) {
          setErrorMsg(`Connection refused. Is the local service running?`);
        } else {
          setErrorMsg(err.message);
        }
      }
    };

    init();

    return () => {
      if (bridge) {
        bridge.stop();
      }
    };
  }, [port]);

  return (
    <Box flexDirection="column" padding={1} borderStyle="round" borderColor={status === 'online' ? 'green' : 'gray'}>
      <Box marginBottom={1}>
        <Text bold color="cyan">Proxiee Tunnel</Text>
        <Text color="gray"> - Local port {port}</Text>
      </Box>

      <Box flexDirection="row" marginBottom={1}>
        <Text>Status: </Text>
        {status === 'connecting' && <Text color="yellow">Connecting to bridge...</Text>}
        {status === 'online' && <Text color="green">● Online</Text>}
        {status === 'error' && <Text color="red">✖ Error: {errorMsg}</Text>}
      </Box>

      {tunnelInfo && tunnelInfo.publicHost && (
        <Box marginBottom={1}>
          <Text>Forwarding: </Text>
          <Text color="blue" underline>https://{tunnelInfo.publicHost}</Text>
          <Text> {'->'} </Text>
          <Text color="gray">http://localhost:{port}</Text>
        </Box>
      )}

      {logDir && (
        <Box marginBottom={1}>
          <Text color="gray">Logging to: </Text>
          <Text color="cyan">{path.resolve(logDir)}/{new Date().toISOString().slice(0, 10)}-log.txt</Text>
        </Box>
      )}

      {tunnelInfo && (
        <Box flexDirection="column" marginTop={1}>
          <Text bold>Traffic Logs:</Text>
          {logs.length === 0 ? (
            <Text color="gray">No requests yet...</Text>
          ) : (
            logs.map((log, index) => (
              <Text key={index}>{log}</Text>
            ))
          )}
        </Box>
      )}
    </Box>
  );
};

export default App;
