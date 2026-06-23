import { useState, useEffect, useCallback, useRef } from 'react';
import WebSocketService from '../services/WebSocketService';
import { MOCK_DATA } from '../data';

const CHANNELS = ['prices', 'positions', 'signals', 'trades', 'grid', 'logs'];

let mountCount = 0;

export default function useBotData() {
  const [connected, setConnected] = useState(false);
  const [data, setData] = useState(MOCK_DATA);
  const handlersRef = useRef(new Map());

  useEffect(() => {
    const ws = WebSocketService.getInstance();
    mountCount++;

    CHANNELS.forEach(channel => {
      const handler = (newData) => {
        setData(prev => {
          if (channel === 'logs') {
            return { ...prev, logs: newData };
          }
          if (channel === 'grid') {
            return { ...prev, gridBot: newData };
          }
          if (channel === 'prices') {
            if (Array.isArray(newData)) {
              return { ...prev, pairs: newData };
            }
            return { ...prev, ...newData };
          }
          return { ...prev, [channel]: newData };
        });
      };
      handlersRef.current.set(channel, handler);
      ws.on(channel, handler);
      ws.subscribe(channel);
    });

    const unsub = ws.onStateChange((state) => {
      setConnected(state === 'connected');
    });

    const fallbackTimer = setTimeout(() => {
      if (ws.getState() !== 'connected') {
        ws.disconnect();
      }
    }, 3000);

    ws.connect();

    return () => {
      clearTimeout(fallbackTimer);
      const handlers = handlersRef.current;
      CHANNELS.forEach(channel => {
        const handler = handlers.get(channel);
        if (handler) {
          ws.off(channel, handler);
        }
        ws.unsubscribe(channel);
      });
      handlers.clear();
      unsub();
      mountCount--;
      if (mountCount === 0) {
        ws.disconnect();
      }
    };
  }, []);

  const send = useCallback((command) => {
    WebSocketService.getInstance().send(command);
  }, []);

  return { data, connected, send };
}
