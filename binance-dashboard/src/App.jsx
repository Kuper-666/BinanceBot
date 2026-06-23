import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import './i18n';
import LanguageSwitcher from './components/LanguageSwitcher';
import NavBar from './components/NavBar';
import ConnectionStatus from './components/ConnectionStatus';
import ErrorBoundary from './components/ErrorBoundary';
import OverviewPage from './pages/OverviewPage';
import SignalsPage from './pages/SignalsPage';
import TradesPage from './pages/TradesPage';
import AnalyticsPage from './pages/AnalyticsPage';
import SettingsPage from './pages/SettingsPage';
import LogsPage from './pages/LogsPage';
import AlertsPage from './pages/AlertsPage';
import GridBotPage from './pages/GridBotPage';
import PositionsPage from './pages/PositionsPage';
import BacktestPage from './pages/BacktestPage';
import useBotData from './hooks/useBotData';

export default function App() {
  const { t } = useTranslation();
  const { data, connected, send } = useBotData();

  return (
    <BrowserRouter>
      <div style={{ minHeight: '100vh', background: '#0a0a0a', color: '#e5e5e5', fontFamily: "'Inter', sans-serif" }}>
        <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '12px 24px', background: '#111', borderBottom: '1px solid #222' }}>
          <h1 style={{ margin: 0, fontSize: '18px', color: '#22c55e' }}>{'\u{1F916}'} {t('app_title')}</h1>
          <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
            <ConnectionStatus connected={connected} />
            <LanguageSwitcher />
          </div>
        </header>

        <NavBar />

        <main style={{ padding: '20px 24px', maxWidth: '1400px', margin: '0 auto' }}>
          <ErrorBoundary>
            <Routes>
              <Route path="/overview" element={<OverviewPage data={data} />} />
              <Route path="/signals" element={<SignalsPage data={data} />} />
              <Route path="/positions" element={<PositionsPage data={data} />} />
              <Route path="/trades" element={<TradesPage data={data} />} />
              <Route path="/grid_bot" element={<GridBotPage data={data} />} />
              <Route path="/analytics" element={<AnalyticsPage data={data} />} />
              <Route path="/backtest" element={<BacktestPage data={data} />} />
              <Route path="/settings" element={<SettingsPage send={send} />} />
              <Route path="/logs" element={<LogsPage data={data} />} />
              <Route path="/alerts" element={<AlertsPage />} />
              <Route path="*" element={<Navigate to="/overview" replace />} />
            </Routes>
          </ErrorBoundary>
        </main>
      </div>
    </BrowserRouter>
  );
}
