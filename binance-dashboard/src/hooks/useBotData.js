import { useState, useEffect, useCallback, useRef } from 'react';
import WebSocketService from '../services/WebSocketService';
import { MOCK_DATA } from '../data';

const CHANNELS = ['prices', 'positions', 'signals', 'trades', 'grid', 'logs'];

let mountCount = 0;

const PAIR_DEFAULTS = {
  signal: 'hold', rsi: 50, macd: 0, bb: 0.02, atr: 0.02, volume: 1, lsma: 0, aiScore: 0.5, risk: 'medium',
};

const POSITION_DEFAULTS = {
  side: 'LONG', current: 0, leverage: 1, pnl: 0, pnlPercent: 0, margin: 0,
  openTime: '--', duration: '--', slPercent: 0, tpPercent: 0,
  echelons: { adaptive: 0, validator: 0, newsSentinel: 'unknown' },
};

function mergePairs(realPairs, mockPairs) {
  return realPairs.map(rp => {
    const mock = mockPairs.find(mp => mp.pair === rp.pair);
    const filled = { ...PAIR_DEFAULTS, ...(mock || {}), ...rp };
    if (!filled.lsma) filled.lsma = filled.price;
    return filled;
  });
}

function mergePositions(realPositions, mockPositions) {
  return realPositions.map(rp => {
    const mock = mockPositions.find(mp => mp.pair === rp.pair);
    const filled = { ...POSITION_DEFAULTS, ...(mock || {}), ...rp };
    if (!filled.current) filled.current = filled.entry;
    if (!filled.side) filled.side = 'LONG';
    return filled;
  });
}

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
              return { ...prev, pairs: mergePairs(newData, prev.pairs) };
            }
            return { ...prev, ...newData };
          }
          if (channel === 'positions') {
            if (Array.isArray(newData)) {
              return { ...prev, positions: mergePositions(newData, prev.positions || []) };
            }
            return { ...prev, [channel]: newData };
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
