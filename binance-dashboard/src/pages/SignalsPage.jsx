import { useTranslation } from 'react-i18next';
import { signalColor, riskColor } from '../data';

export default function SignalsPage({ data }) {
  const { t } = useTranslation();

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      {data.pairs.map(p => (
        <div key={p.pair} style={{ background: '#141414', borderRadius: '8px', padding: '16px', border: '1px solid #222' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
            <div>
              <span style={{ fontSize: '18px', fontWeight: 700 }}>{p.pair}</span>
              <span style={{ marginLeft: '12px', padding: '3px 10px', borderRadius: '4px', background: signalColor(p.signal) + '22', color: signalColor(p.signal), fontWeight: 600 }}>
                {t(p.signal)}
              </span>
            </div>
            <div style={{ fontSize: '20px', fontFamily: 'monospace' }}>${p.price.toLocaleString()}</div>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(100px, 1fr))', gap: '12px' }}>
            {[
              { label: 'RSI', value: p.rsi.toFixed(1), color: p.rsi < 30 ? '#22c55e' : p.rsi > 70 ? '#ef4444' : '#ccc' },
              { label: 'MACD', value: p.macd.toFixed(4), color: p.macd > 0 ? '#22c55e' : '#ef4444' },
              { label: 'BB', value: (p.bb * 100).toFixed(1) + '%' },
              { label: 'ATR', value: (p.atr * 100).toFixed(1) + '%' },
              { label: t('volume'), value: p.volume.toFixed(2) + 'x', color: p.volume > 1.5 ? '#f59e0b' : '#ccc' },
              { label: 'LSMA', value: '$' + p.lsma.toLocaleString() },
              { label: t('ai_score'), value: (p.aiScore * 100).toFixed(0) + '%', color: p.aiScore > 0.6 ? '#22c55e' : p.aiScore < 0.4 ? '#ef4444' : '#f59e0b' },
              { label: t('risk_level'), value: t(p.risk + '_risk'), color: riskColor(p.risk) },
            ].map(item => (
              <div key={item.label} style={{ textAlign: 'center', padding: '8px', background: '#1a1a1a', borderRadius: '6px' }}>
                <div style={{ fontSize: '11px', color: '#666' }}>{item.label}</div>
                <div style={{ fontSize: '15px', fontWeight: 600, color: item.color || '#fff', marginTop: '2px' }}>{item.value}</div>
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
