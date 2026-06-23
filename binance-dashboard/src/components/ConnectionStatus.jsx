export default function ConnectionStatus({ connected }) {
  const color = connected ? '#22c55e' : '#ef4444';
  const label = connected ? 'Connected' : 'Disconnected (mock data)';

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: '8px', fontSize: '12px', color: '#999' }}>
      <span style={{
        width: '8px',
        height: '8px',
        borderRadius: '50%',
        background: color,
        boxShadow: `0 0 6px ${color}55`,
      }} />
      <span>{label}</span>
    </div>
  );
}
