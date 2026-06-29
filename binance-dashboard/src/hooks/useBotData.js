import { useState, useEffect, useCallback, useRef } from 'react';
import WebSocketService from '../services/WebSocketService';
import { MOCK_DATA } from '../data';

const CHANNELS = ['prices', 'positions', 'trades', 'stats', 'echelons', 'equity', 'feargreed', 'pnl', 'gridbot', 'backtest'];

let mountCount = 0;

const PAIR_DEFAULTS = {
  signal: 'hold', rsi: 50, macd: 0, bb: 0.02, atr: 0.02, volume: 1, lsma: 0, aiScore: 0.5, risk: 'medium',
};

const POSITION_DEFAULTS = {
  side: 'LONG', current: 0, leverage: 1, pnl: 0, pnlPercent: 0, margin: 0,
  openTime: '--', duration: '--', slPercent: 0, tpPercent: 0,
  echelons: { adaptive: 0, validator: 0, newsSentinel: 'unknown' },
};

function mapAnalysis(rp) {
  const mapped = { ...rp };
  if (rp.macdHist !== undefined) mapped.macd = rp.macdHist;
  if (rp.bbWidth !== undefined) mapped.bb = rp.bbWidth;
  if (rp.volumeRatio !== undefined) mapped.volume = rp.volumeRatio;
  if (rp.aiProbability !== undefined) mapped.aiScore = rp.aiProbability;
  if (rp.aiRiskLevel !== undefined) mapped.risk = rp.aiRiskLevel;
  if (rp.action) mapped.signal = rp.action.toLowerCase();
  return mapped;
}

function mergePairs(realPairs, mockPairs) {
  return realPairs.map(rp => {
    const mapped = mapAnalysis(rp);
    const mock = mockPairs.find(mp => mp.pair === mapped.pair);
    const filled = { ...PAIR_DEFAULTS, ...(mock || {}), ...mapped };
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
  const [data, setData] = useState({
    balance: 0, pnl: 0, pnlPercent: 0, winRate: 0, maxDrawdown: 0,
    totalTrades: 0, openPositions: 0, maxPositions: 5,
    winningTrades: 0, losingTrades: 0, bestPnL: 0, worstPnL: 0,
    fearGreedValue: 50, fearGreedClassification: 'Neutral', leverage: 5,
    pairs: MOCK_DATA.pairs,
    echelons: { adaptive: true, validator: true, newsSentinel: true },
    equity: [],
    pnlHistory: [],
    news: [],
    trades: [],
    positions: [],
    gridBot: { enabled: false, running: false, orders: [], pair: '', levels: 0, filledOrders: 0, rangeLow: 0, rangeHigh: 0, investment: 0, investmentPercent: 0, realizedPnl: 0, unrealizedPnl: 0 },
    backtest: null,
  });
  const handlersRef = useRef(new Map());

  useEffect(() => {
    const ws = WebSocketService.getInstance();
    mountCount++;

    const handlers = handlersRef.current;

    CHANNELS.forEach(channel => {
      const handler = (newData) => {
        setData(prev => {
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
          if (channel === 'stats') {
            return { ...prev, ...newData };
          }
          if (channel === 'trades') {
            if (Array.isArray(newData)) {
              return { ...prev, trades: newData };
            }
            return { ...prev, [channel]: newData };
          }
          if (channel === 'echelons') {
            return { ...prev, echelons: newData };
          }
          if (channel === 'equity') {
            if (Array.isArray(newData)) {
              return { ...prev, equity: newData };
            }
            return { ...prev, equity: [] };
          }
          if (channel === 'feargreed') {
            return { ...prev, fearGreedValue: newData.value, fearGreedClassification: newData.classification };
          }
          if (channel === 'pnl') {
            if (Array.isArray(newData)) {
              return { ...prev, pnlHistory: newData };
            }
            return { ...prev, pnlHistory: [] };
          }
          if (channel === 'gridbot') {
            return { ...prev, gridBot: newData };
          }
          if (channel === 'backtest') {
            return { ...prev, backtest: newData };
          }
          return { ...prev, [channel]: newData };
        });
      };
      handlers.set(channel, handler);
      ws.on(channel, handler);
      ws.subscribe(channel);
    });

    const unsub = ws.onStateChange((state) => {
      setConnected(state === 'connected');
      if (state === 'connected') {
        clearTimeout(fallbackTimer);
      }
    });

    ws.connect();

    const fallbackTimer = setTimeout(() => {
      if (ws.getState() !== 'connected') {
        ws.disconnect();
      }
    }, 10000);

    return () => {
      clearTimeout(fallbackTimer);
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
