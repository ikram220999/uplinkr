import React, { useState, useEffect } from 'react';
import { Box, Text } from 'ink';
import { BridgeClient } from './bridge.js';

const App = ({ port }) => {
  const [status, setStatus] = useState('registering'); // registering | connecting | online | error
  const [tunnelInfo, setTunnelInfo] = useState(null);
  const [errorMsg, setErrorMsg] = useState('');
  const [logs, setLogs] = useState([]);

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
        setStatus('registering');
        const res = await fetch('http://127.0.0.1:5099/api/Tunnels/register', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ LocalPort: parseInt(port, 10) })
        });

        if (!res.ok) {
          const text = await res.text();
          throw new Error(`Failed to register tunnel: ${res.status} - ${text}`);
        }

        const data = await res.json();
        setTunnelInfo(data);
        setStatus('connecting');

        const bridgeUrl = data.webSocketServerUrl || data.WebSocketServerUrl; // Handle different casing
        if (!bridgeUrl) throw new Error("No WebSocket URL received from API");
        console.log(bridgeUrl);
        bridge = new BridgeClient("ws://localhost:4001", `http://localhost:${port}`, {
          onConnect: () => setStatus('online'),
          onClose: () => {
            if (status !== 'error') setStatus('connecting');
          },
          onRequest: ({ method, path }) => { },
          onResponse: ({ method, path, status, durationMs }) => {
            const timeStr = new Date().toLocaleTimeString();
            const color = status >= 500 ? 'red' : status >= 400 ? 'yellow' : 'green';
            addLog(`[${timeStr}] ${method} ${path} -> ${status} (${durationMs}ms)`);
          },
          onError: (msg) => {
            // We only log the error but don't crash, the bridge auto-reconnects
            addLog(`[ERROR] ${msg}`);
          }
        });

        bridge.runForever();
      } catch (err) {
        setStatus('error');
        if (err.message.includes('fetch failed')) {
          setErrorMsg(`Connection refused. Is the Core API running on http://127.0.0.1:5099?`);
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
        {status === 'registering' && <Text color="yellow">Registering tunnel...</Text>}
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
