import { useTranslation } from 'react-i18next';
import { AreaChart, Area, XAxis, YAxis, Tooltip, ResponsiveContainer, BarChart, Bar, Cell, CartesianGrid } from 'recharts';
import StatCard from '../components/StatCard';

const CustomTooltip = ({ active, payload, label }) => {
  if (!active || !payload?.length) return null;
  return (
    <div style={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', padding: '8px 12px', fontSize: '12px' }}>
      <div style={{ color: '#888' }}>{label}</div>
      <div style={{ color: '#22c55e', fontWeight: 600 }}>${payload[0].value.toLocaleString()}</div>
    </div>
  );
};

export default function BacktestPage({ data }) {
  const { t } = useTranslation();
  const bt = data.backtest;
  const equityData = Array.isArray(bt?.equity) ? bt.equity : [];

  if (!bt) {
    return (
      <div style={{ display: 'grid', gap: '16px' }}>
        <h3 style={{ fontSize: '16px', fontWeight: 700 }}>{t('backtest')}</h3>
        <div className="card" style={{ padding: '32px', textAlign: 'center' }}>
          <div style={{ fontSize: '36px', marginBottom: '12px' }}>📊</div>
          <div style={{ fontSize: '14px', color: '#888', marginBottom: '8px' }}>{t('no_data')}</div>
          <div style={{ fontSize: '12px', color: '#666' }}>
            {t('backtest_not_run')}
          </div>
        </div>
      </div>
    );
  }

  const sp = bt.strategyParams || {};
  const monthly = Array.isArray(bt.monthlyReturns) ? bt.monthlyReturns : [];
  const sharpeVal = typeof bt.sharpeRatio === 'number' ? bt.sharpeRatio : parseFloat(bt.sharpeRatio) || 0;
  const pfVal = typeof bt.profitFactor === 'number' ? bt.profitFactor : parseFloat(bt.profitFactor) || 0;

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h3 style={{ fontSize: '16px', fontWeight: 700 }}>{t('backtest')}</h3>
        <div style={{ fontSize: '12px', color: '#888' }}>{bt.startDate} — {bt.endDate}</div>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '12px' }}>
        <StatCard label={t('initial_balance')} value={`$${bt.initialBalance}`} />
        <StatCard label={t('final_balance')} value={`$${bt.finalBalance.toLocaleString()}`} color="#22c55e" />
        <StatCard label={t('total_return')} value={`+${bt.totalReturn}%`} color="#22c55e" />
        <StatCard label={t('max_drawdown')} value={`-${bt.maxDrawdown}%`} color="#ef4444" />
        <StatCard label={t('sharpe_ratio')} value={sharpeVal.toFixed(2)} color={sharpeVal > 1.5 ? '#22c55e' : '#f59e0b'} />
        <StatCard label={t('win_rate')} value={`${bt.winRate}%`} />
        <StatCard label={t('total_trades')} value={bt.totalTrades} />
        <StatCard label={t('profit_factor')} value={pfVal.toFixed(2)} color="#22c55e" />
        <StatCard label={t('avg_win')} value={`+${bt.avgWin}%`} color="#22c55e" />
        <StatCard label={t('avg_loss')} value={`${bt.avgLoss}%`} color="#ef4444" />
      </div>

      <div className="page-grid-2">
        <div className="card">
          <h3>{t('equity_curve')}</h3>
          <ResponsiveContainer width="100%" height={220}>
            <AreaChart data={equityData} margin={{ top: 5, right: 5, left: 5, bottom: 5 }}>
              <defs>
                <linearGradient id="btEqGrad" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor="#22c55e" stopOpacity={0.3} />
                  <stop offset="100%" stopColor="#22c55e" stopOpacity={0} />
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" stroke="#1a1a1a" />
              <XAxis dataKey="date" tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} />
              <YAxis tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} domain={['dataMin - 30', 'dataMax + 30']} />
              <Tooltip content={<CustomTooltip />} />
              <Area type="monotone" dataKey="value" stroke="#22c55e" strokeWidth={2} fill="url(#btEqGrad)" />
            </AreaChart>
          </ResponsiveContainer>
        </div>

        <div className="card">
          <h3>{t('monthly_returns')}</h3>
          {monthly.length > 0 ? (
          <ResponsiveContainer width="100%" height={220}>
            <BarChart data={monthly} margin={{ top: 10, right: 10, left: 10, bottom: 10 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#1a1a1a" />
              <XAxis dataKey="month" tick={{ fill: '#666', fontSize: 12 }} axisLine={false} tickLine={false} />
              <YAxis tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} tickFormatter={v => `${v}%`} />
              <Tooltip
                contentStyle={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', fontSize: '12px' }}
                formatter={(v) => [`+${v}%`, t('return')]}
                labelStyle={{ color: '#888' }}
              />
              <Bar dataKey="return" radius={[4, 4, 0, 0]}>
                {monthly.map((entry, i) => (
                  <Cell key={i} fill={entry.return > 5 ? '#22c55e' : entry.return > 3 ? '#16a34a' : '#15803d'} />
                ))}
              </Bar>
            </BarChart>
          </ResponsiveContainer>
          ) : (
          <div style={{ height: 220, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#666', fontSize: '12px' }}>
            {t('no_data')}
          </div>
          )}
        </div>
      </div>

      <div className="card">
        <h3>{t('backtest_summary')}</h3>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(250px, 1fr))', gap: '16px' }}>
          <div>
            <div style={{ fontSize: '12px', color: '#888', marginBottom: '8px' }}>{t('strategy_params')}</div>
            {[
              { label: t('fast_sma'), value: sp.fastSma || '9' },
              { label: t('slow_sma'), value: sp.slowSma || '21' },
              { label: t('rsi_period'), value: sp.rsiPeriod || '14' },
              { label: t('stop_loss'), value: sp.stopLoss || 'ATR × 1.5' },
              { label: t('take_profit'), value: sp.takeProfit || 'SL × 3' },
              { label: t('adaptive_sl'), value: sp.adaptiveSl || '0.4x' },
            ].map(s => (
              <div key={s.label} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid #1a1a1a', fontSize: '12px' }}>
                <span style={{ color: '#aaa' }}>{s.label}</span>
                <span style={{ fontWeight: 600 }}>{s.value}</span>
              </div>
            ))}
          </div>
          <div>
            <div style={{ fontSize: '12px', color: '#888', marginBottom: '8px' }}>{t('performance_metrics')}</div>
            {[
              { label: t('total_return'), value: `+${bt.totalReturn}%`, color: '#22c55e' },
              { label: t('win_rate'), value: `${bt.winRate}%` },
              { label: t('profit_factor'), value: pfVal.toFixed(2), color: '#22c55e' },
              { label: t('sharpe_ratio'), value: sharpeVal.toFixed(2) },
              { label: t('max_drawdown'), value: `-${bt.maxDrawdown}%`, color: '#ef4444' },
              { label: t('total_trades'), value: bt.totalTrades },
            ].map(s => (
              <div key={s.label} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid #1a1a1a', fontSize: '12px' }}>
                <span style={{ color: '#aaa' }}>{s.label}</span>
                <span style={{ fontWeight: 600, color: s.color || '#fff' }}>{s.value}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
