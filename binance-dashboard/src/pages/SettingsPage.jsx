import { useState } from 'react';
import { useTranslation } from 'react-i18next';

const DEFAULT_SETTINGS = {
  strategy: {
    fastSma: 9,
    slowSma: 21,
    rsiPeriod: 14,
    stopLoss: 2.0,
    takeProfit: 6.0,
    riskPerTrade: 1.0,
    maxPositions: 5,
    leverage: 5,
  },
  echelons: {
    adaptiveAgent: true,
    adaptiveSlMult: 0.4,
    adaptivePeriodMult: 0.3,
    signalValidator: true,
    validatorVolThreshold: 8.0,
    validatorAtrThreshold: 0.15,
    validatorRsiLow: 20,
    validatorRsiHigh: 80,
    newsSentinel: true,
    newsBlockMinutes: 5,
  },
  gridBot: {
    enabled: false,
    defaultPairs: 'ETHUSDT,BTCUSDT',
    investmentPercent: 20,
    levels: 8,
    rangePercent: 5.0,
  },
};

function Toggle({ value, onChange }) {
  return (
    <div
      role="switch"
      aria-checked={value}
      tabIndex={0}
      onClick={() => onChange(!value)}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onChange(!value); } }}
      style={{
        width: '36px', height: '20px', borderRadius: '10px', cursor: 'pointer', transition: 'all 0.2s',
        background: value ? '#22c55e' : '#333', position: 'relative', flexShrink: 0, outline: 'none',
      }}
    >
      <div style={{
        width: '16px', height: '16px', borderRadius: '50%', background: '#fff',
        position: 'absolute', top: '2px', left: value ? '18px' : '2px', transition: 'all 0.2s',
      }} />
    </div>
  );
}

function NumberInput({ value, onChange, min, max, step = 1, suffix = '' }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
      <button onClick={() => onChange(Math.max(min, value - step))}
        style={{ width: '24px', height: '24px', borderRadius: '4px', border: '1px solid #333', background: '#1a1a1a', color: '#888', cursor: 'pointer', fontSize: '12px' }}>-</button>
      <input type="number" value={value} onChange={e => onChange(Number(e.target.value))}
        min={min} max={max} step={step}
        style={{ width: '56px', padding: '4px', textAlign: 'center', borderRadius: '4px', border: '1px solid #333', background: '#1a1a1a', color: '#e5e5e5', fontSize: '13px', fontFamily: 'monospace', outline: 'none' }} />
      <button onClick={() => onChange(Math.min(max, value + step))}
        style={{ width: '24px', height: '24px', borderRadius: '4px', border: '1px solid #333', background: '#1a1a1a', color: '#888', cursor: 'pointer', fontSize: '12px' }}>+</button>
      {suffix && <span style={{ fontSize: '11px', color: '#666' }}>{suffix}</span>}
    </div>
  );
}

function SettingRow({ label, children }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '8px 0', borderBottom: '1px solid #1a1a1a' }}>
      <span style={{ color: '#aaa', fontSize: '13px' }}>{label}</span>
      {children}
    </div>
  );
}

export default function SettingsPage({ send, data }) {
  const { t } = useTranslation();
  const [settings, setSettings] = useState(DEFAULT_SETTINGS);
  const [saved, setSaved] = useState(false);
  const [initialized, setInitialized] = useState(false);

  if (!initialized && data) {
    const s = { ...DEFAULT_SETTINGS };
    if (data.fastSma) s.strategy.fastSma = data.fastSma;
    if (data.slowSma) s.strategy.slowSma = data.slowSma;
    if (data.rsiPeriod) s.strategy.rsiPeriod = data.rsiPeriod;
    if (data.stopLossPercent) s.strategy.stopLoss = Math.round(data.stopLossPercent * 100);
    if (data.takeProfitPercent) s.strategy.takeProfit = Math.round(data.takeProfitPercent * 100);
    if (data.riskPerTradePercent) s.strategy.riskPerTrade = Math.round(data.riskPerTradePercent * 100);
    if (data.maxPositions) s.strategy.maxPositions = data.maxPositions;
    if (data.leverage) s.strategy.leverage = data.leverage;
    if (data.adaptiveAgentEnabled !== undefined) s.echelons.adaptiveAgent = data.adaptiveAgentEnabled;
    if (data.signalValidatorEnabled !== undefined) s.echelons.signalValidator = data.signalValidatorEnabled;
    if (data.newsSentinelEnabled !== undefined) s.echelons.newsSentinel = data.newsSentinelEnabled;
    if (data.gridBotEnabled !== undefined) s.gridBot.enabled = data.gridBotEnabled;
    if (data.gridSymbol) s.gridBot.defaultPairs = data.gridSymbol;
    if (data.gridRangePercent) s.gridBot.rangePercent = data.gridRangePercent;
    if (data.gridLevels) s.gridBot.levels = data.gridLevels;
    if (data.gridInvestmentPercent) s.gridBot.investmentPercent = data.gridInvestmentPercent;
    setSettings(s);
    setInitialized(true);
  }

  const updateStrategy = (key, value) => {
    setSettings(s => ({ ...s, strategy: { ...s.strategy, [key]: value } }));
    setSaved(false);
  };
  const updateEchelon = (key, value) => {
    setSettings(s => ({ ...s, echelons: { ...s.echelons, [key]: value } }));
    setSaved(false);
  };
  const updateGrid = (key, value) => {
    setSettings(s => ({ ...s, gridBot: { ...s.gridBot, [key]: value } }));
    setSaved(false);
  };

  const handleSave = () => {
    if (send) {
      send({ type: 'settings', data: settings });
    }
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  const handleStart = () => { if (send) send({ type: 'command', action: 'start' }); };
  const handleStop = () => { if (send) send({ type: 'command', action: 'stop' }); };
  const handleRetrain = () => { if (send) send({ type: 'command', action: 'retrain' }); };
  const handleBacktest = () => { if (send) send({ type: 'command', action: 'backtest' }); };
  const handleExport = () => { if (send) send({ type: 'command', action: 'export' }); };

  return (
    <div style={{ display: 'grid', gap: '16px', gridTemplateColumns: '1fr 1fr' }}>
      <div className="card">
        <h3>{t('strategy')}</h3>
        <SettingRow label={t('fast_sma')}><NumberInput value={settings.strategy.fastSma} onChange={v => updateStrategy('fastSma', v)} min={3} max={50} /></SettingRow>
        <SettingRow label={t('slow_sma')}><NumberInput value={settings.strategy.slowSma} onChange={v => updateStrategy('slowSma', v)} min={10} max={200} /></SettingRow>
        <SettingRow label={t('rsi_period')}><NumberInput value={settings.strategy.rsiPeriod} onChange={v => updateStrategy('rsiPeriod', v)} min={5} max={30} /></SettingRow>
        <SettingRow label={t('stop_loss')}><NumberInput value={settings.strategy.stopLoss} onChange={v => updateStrategy('stopLoss', v)} min={0.5} max={10} step={0.5} suffix="%" /></SettingRow>
        <SettingRow label={t('take_profit')}><NumberInput value={settings.strategy.takeProfit} onChange={v => updateStrategy('takeProfit', v)} min={1} max={20} step={0.5} suffix="%" /></SettingRow>
        <SettingRow label={t('risk_per_trade')}><NumberInput value={settings.strategy.riskPerTrade} onChange={v => updateStrategy('riskPerTrade', v)} min={0.1} max={5} step={0.1} suffix="%" /></SettingRow>
        <SettingRow label={t('max_positions')}><NumberInput value={settings.strategy.maxPositions} onChange={v => updateStrategy('maxPositions', v)} min={1} max={20} /></SettingRow>
        <SettingRow label={t('leverage')}><NumberInput value={settings.strategy.leverage} onChange={v => updateStrategy('leverage', v)} min={1} max={50} suffix="x" /></SettingRow>
      </div>

      <div className="card">
        <h3>{t('echelons')}</h3>
        <SettingRow label={t('echelon_1')}>
          <Toggle value={settings.echelons.adaptiveAgent} onChange={v => updateEchelon('adaptiveAgent', v)} />
        </SettingRow>
        <SettingRow label={t('sl_multiplier')}>
          <NumberInput value={settings.echelons.adaptiveSlMult} onChange={v => updateEchelon('adaptiveSlMult', v)} min={0.1} max={2.0} step={0.1} />
        </SettingRow>
        <SettingRow label={t('period_multiplier')}>
          <NumberInput value={settings.echelons.adaptivePeriodMult} onChange={v => updateEchelon('adaptivePeriodMult', v)} min={0.1} max={2.0} step={0.1} />
        </SettingRow>
        <div style={{ borderBottom: '1px solid #222', margin: '8px 0' }} />
        <SettingRow label={t('echelon_2')}>
          <Toggle value={settings.echelons.signalValidator} onChange={v => updateEchelon('signalValidator', v)} />
        </SettingRow>
        <SettingRow label={t('vol_threshold')}>
          <NumberInput value={settings.echelons.validatorVolThreshold} onChange={v => updateEchelon('validatorVolThreshold', v)} min={1} max={20} step={0.5} />
        </SettingRow>
        <SettingRow label={t('atr_threshold')}>
          <NumberInput value={settings.echelons.validatorAtrThreshold} onChange={v => updateEchelon('validatorAtrThreshold', v)} min={0.01} max={1.0} step={0.01} />
        </SettingRow>
        <SettingRow label={t('rsi_range')}>
          <span style={{ fontSize: '12px', fontFamily: 'monospace', color: '#22c55e' }}>{settings.echelons.validatorRsiLow} / {settings.echelons.validatorRsiHigh}</span>
        </SettingRow>
        <div style={{ borderBottom: '1px solid #222', margin: '8px 0' }} />
        <SettingRow label={t('echelon_3')}>
          <Toggle value={settings.echelons.newsSentinel} onChange={v => updateEchelon('newsSentinel', v)} />
        </SettingRow>
        <SettingRow label={t('block_minutes')}>
          <NumberInput value={settings.echelons.newsBlockMinutes} onChange={v => updateEchelon('newsBlockMinutes', v)} min={1} max={30} suffix="min" />
        </SettingRow>
      </div>

      <div className="card">
        <h3>{t('grid_bot')}</h3>
        <SettingRow label={t('enabled')}>
          <Toggle value={settings.gridBot.enabled} onChange={v => updateGrid('enabled', v)} />
        </SettingRow>
        <SettingRow label={t('grid_levels')}>
          <NumberInput value={settings.gridBot.levels} onChange={v => updateGrid('levels', v)} min={4} max={20} />
        </SettingRow>
        <SettingRow label={t('investment')}>
          <NumberInput value={settings.gridBot.investmentPercent} onChange={v => updateGrid('investmentPercent', v)} min={5} max={50} step={5} suffix="%" />
        </SettingRow>
        <SettingRow label={t('grid_range')}>
          <NumberInput value={settings.gridBot.rangePercent} onChange={v => updateGrid('rangePercent', v)} min={1} max={20} step={0.5} suffix="%" />
        </SettingRow>
        <SettingRow label={t('pairs')}>
          <span style={{ fontSize: '12px', fontFamily: 'monospace', color: '#22c55e' }}>{settings.gridBot.defaultPairs}</span>
        </SettingRow>
      </div>

      <div className="card" style={{ display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
        <div>
          <h3>{t('bot_control')}</h3>
          <div style={{ display: 'flex', gap: '8px', marginTop: '12px', flexWrap: 'wrap' }}>
            <button className="btn btn-primary" onClick={handleStart}>{t('start')}</button>
            <button className="btn btn-danger" onClick={handleStop}>{t('stop')}</button>
            <button className="btn btn-outline" onClick={handleBacktest}>{t('backtest')}</button>
            <button className="btn btn-outline" onClick={handleRetrain}>{t('retrain')}</button>
          </div>
          <div style={{ marginTop: '16px', padding: '12px', background: '#1a1a1a', borderRadius: '6px', fontSize: '12px' }}>
            <div style={{ color: '#888', marginBottom: '4px' }}>{t('bot_status')}</div>
            <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
              <div style={{ width: '8px', height: '8px', borderRadius: '50%', background: '#22c55e' }} />
              <span style={{ color: '#22c55e', fontWeight: 600 }}>{t('running')}</span>
              <span style={{ color: '#666', marginLeft: '8px' }}>{t('version_info')}</span>
            </div>
          </div>
        </div>
        <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end', marginTop: '16px' }}>
          <button className="btn btn-outline" onClick={handleExport}>{t('export')}</button>
          <button className="btn btn-primary" onClick={handleSave} style={{
            background: saved ? '#16a34a' : '#22c55e',
            transition: 'all 0.2s',
          }}>{saved ? t('saved') : t('save')}</button>
        </div>
      </div>
    </div>
  );
}
