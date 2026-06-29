import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ScatterChart, Scatter, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid, Cell, PieChart, Pie } from 'recharts';

function generateCorrelation(pairNames) {
  const seeded = pairNames.join('').length;
  return pairNames.map((a, i) => pairNames.map((b, j) => {
    if (i === j) return '1.00';
    const seed = (seeded * 31 + i * 7 + j * 13) % 100;
    return (seed / 100 * 0.8 - 0.2).toFixed(2);
  }));
}

export default function AnalyticsPage({ data }) {
  const { t } = useTranslation();
  const pairsData = Array.isArray(data.pairs) ? data.pairs : [];

  const pairs = useMemo(() => pairsData.map(p => p.pair), [pairsData]);
  const corr = useMemo(() => generateCorrelation(pairs), [pairs]);

  const riskDistribution = [
    { name: t('low_risk'), value: pairsData.filter(p => p.risk === 'low').length, color: '#22c55e' },
    { name: t('medium_risk'), value: pairsData.filter(p => p.risk === 'medium').length, color: '#f59e0b' },
    { name: t('high_risk'), value: pairsData.filter(p => p.risk === 'high').length, color: '#ef4444' },
  ];

  const scatterData = pairsData.map(p => ({
    name: p.pair.replace('USDT', ''),
    rsi: p.rsi,
    aiScore: p.aiScore,
    volume: p.volume,
    atr: p.atr * 100,
  }));

  const ScatterTooltip = ({ active, payload }) => {
    if (!active || !payload?.length) return null;
    const d = payload[0].payload;
    return (
      <div style={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', padding: '8px 12px', fontSize: '12px' }}>
        <div style={{ fontWeight: 600, marginBottom: '4px' }}>{d.name}</div>
        <div style={{ color: '#888' }}>RSI: {d.rsi} · AI: {(d.aiScore * 100).toFixed(0)}%</div>
        <div style={{ color: '#888' }}>Volume: {d.volume}x · ATR: {d.atr}%</div>
      </div>
    );
  };

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div className="page-grid-2">
        <div className="card">
          <h3>{t('correlation')}</h3>
          <div style={{ overflowX: 'auto' }}>
            <table style={{ fontSize: '10px' }}>
              <thead><tr>
                <th></th>
                {pairs.map(p => <th key={p} style={{ padding: '4px', color: '#666' }}>{p.replace('USDT', '')}</th>)}
              </tr></thead>
              <tbody>
                {corr.map((row, i) => (
                  <tr key={i}>
                    <td style={{ padding: '4px', color: '#666', fontWeight: 600 }}>{pairs[i].replace('USDT', '')}</td>
                    {row.map((v, j) => {
                      const val = parseFloat(v);
                      const bg = val > 0 ? `rgba(34,197,94,${Math.abs(val) * 0.5})` : `rgba(239,68,68,${Math.abs(val) * 0.5})`;
                      return <td key={j} style={{ padding: '4px', textAlign: 'center', background: bg, borderRadius: '2px' }}>{v}</td>;
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>

        <div className="card">
          <h3>{t('risk_distribution')}</h3>
          <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
            <ResponsiveContainer width={160} height={160}>
              <PieChart>
                <Pie data={riskDistribution} dataKey="value" cx="50%" cy="50%" innerRadius={40} outerRadius={65} strokeWidth={0}>
                  {riskDistribution.map((entry, i) => (
                    <Cell key={i} fill={entry.color} />
                  ))}
                </Pie>
                <Tooltip
                  contentStyle={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', fontSize: '12px' }}
                />
              </PieChart>
            </ResponsiveContainer>
            <div>
              {riskDistribution.map(r => (
                <div key={r.name} style={{ display: 'flex', gap: '8px', alignItems: 'center', padding: '4px 0', fontSize: '12px' }}>
                  <div style={{ width: '10px', height: '10px', borderRadius: '2px', background: r.color }} />
                  <span style={{ color: '#aaa' }}>{r.name}</span>
                  <span style={{ fontWeight: 600 }}>{r.value}</span>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>

      <div className="page-grid-2">
        <div className="card">
          <h3>{t('rsi_vs_ai_score')}</h3>
          <ResponsiveContainer width="100%" height={200}>
            <ScatterChart margin={{ top: 10, right: 10, left: 10, bottom: 10 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#1a1a1a" />
              <XAxis dataKey="rsi" name="RSI" type="number" domain={[0, 100]}
                tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false}
                label={{ value: 'RSI', position: 'bottom', fill: '#666', fontSize: 10 }} />
              <YAxis dataKey="aiScore" name="AI Score" type="number" domain={[0, 1]}
                tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false}
                tickFormatter={v => `${(v * 100).toFixed(0)}%`}
                label={{ value: 'AI %', angle: -90, position: 'insideLeft', fill: '#666', fontSize: 10 }} />
              <Tooltip content={<ScatterTooltip />} />
              <Scatter data={scatterData}>
                {scatterData.map((entry, i) => (
                  <Cell key={i} fill={entry.aiScore > 0.6 ? '#22c55e' : entry.aiScore < 0.4 ? '#ef4444' : '#f59e0b'} />
                ))}
              </Scatter>
            </ScatterChart>
          </ResponsiveContainer>
        </div>

        <div className="card">
          <h3>{t('volatility_heatmap')}</h3>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(80px, 1fr))', gap: '8px' }}>
            {pairsData.map(p => {
              const vol = p.atr * 100;
              const intensity = Math.min(vol / 4, 1);
              return (
                <div key={p.pair} style={{ padding: '12px', borderRadius: '6px', textAlign: 'center',
                  background: `rgba(${Math.round(239 * intensity)}, ${Math.round(68 + 180 * (1 - intensity))}, ${Math.round(94 * (1 - intensity))}, 0.3)`,
                  border: `1px solid rgba(${Math.round(239 * intensity)}, ${Math.round(68 + 180 * (1 - intensity))}, ${Math.round(94 * (1 - intensity))}, 0.5)` }}>
                  <div style={{ fontSize: '12px', fontWeight: 600 }}>{p.pair.replace('USDT', '')}</div>
                  <div style={{ fontSize: '16px', fontWeight: 700, marginTop: '4px' }}>{vol.toFixed(1)}%</div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      <div className="card">
        <h3>{t('pair_details')}</h3>
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead><tr>
              <th>{t('pair')}</th>
              <th style={{ textAlign: 'right' }}>{t('price')}</th>
              <th style={{ textAlign: 'center' }}>{t('rsi')}</th>
              <th style={{ textAlign: 'center' }}>MACD</th>
              <th style={{ textAlign: 'center' }}>{t('bollinger')}</th>
              <th style={{ textAlign: 'center' }}>{t('atr')}</th>
              <th style={{ textAlign: 'center' }}>{t('volume')}</th>
              <th style={{ textAlign: 'center' }}>{t('lsma')}</th>
            </tr></thead>
            <tbody>
              {pairsData.map(p => (
                <tr key={p.pair}>
                  <td style={{ fontWeight: 600 }}>{p.pair}</td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${p.price.toLocaleString()}</td>
                  <td style={{ textAlign: 'center', color: p.rsi < 30 ? '#22c55e' : p.rsi > 70 ? '#ef4444' : '#ccc' }}>{p.rsi.toFixed(1)}</td>
                  <td style={{ textAlign: 'center', color: p.macd > 0 ? '#22c55e' : '#ef4444' }}>{p.macd.toFixed(4)}</td>
                  <td style={{ textAlign: 'center' }}>{(p.bb * 100).toFixed(1)}%</td>
                  <td style={{ textAlign: 'center' }}>{(p.atr * 100).toFixed(1)}%</td>
                  <td style={{ textAlign: 'center', color: p.volume > 1.5 ? '#f59e0b' : '#ccc' }}>{p.volume.toFixed(2)}x</td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${p.lsma.toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
