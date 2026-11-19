import { Navigate, Route, Routes } from 'react-router-dom';
import { Account } from './pages/Account';
import { AuthCallback } from './pages/AuthCallback';
import { CreateEndpoint } from './pages/CreateEndpoint';
import { DeliveryTab } from './pages/endpoint/DeliveryTab';
import { EndpointLayout } from './pages/endpoint/EndpointLayout';
import { EventsTab } from './pages/endpoint/EventsTab';
import { LogsTab } from './pages/endpoint/LogsTab';
import { SecurityTab } from './pages/endpoint/SecurityTab';
import { SettingsTab } from './pages/endpoint/SettingsTab';
import { Logs } from './pages/Logs';
import { Overview } from './pages/Overview';
import { Playground } from './pages/Playground';
import { Replay } from './pages/Replay';
import { Alerts } from './pages/settings/Alerts';
import { ApiKeys } from './pages/settings/ApiKeys';
import { AuditLog } from './pages/settings/AuditLog';
import { Team } from './pages/settings/Team';
import { Register } from './pages/Register';
import { SelectTenant } from './pages/SelectTenant';
import { RequireSession } from './shell/RequireSession';
import { Shell } from './shell/Shell';

export function App() {
  return (
    <Routes>
      <Route path="/select-tenant" element={<SelectTenant />} />
      <Route path="/register" element={<Register />} />
      <Route path="/auth/callback" element={<AuthCallback />} />
      <Route element={<RequireSession />}>
        <Route element={<Shell />}>
          <Route index element={<Overview />} />
          <Route path="account" element={<Account />} />
          <Route path="logs" element={<Logs />} />
          <Route path="playground" element={<Playground />} />
          <Route path="replay" element={<Replay />} />
          <Route path="settings/alerts" element={<Alerts />} />
          <Route path="settings/api-keys" element={<ApiKeys />} />
          <Route path="settings/audit" element={<AuditLog />} />
          <Route path="settings/team" element={<Team />} />
          <Route path="endpoints/new" element={<CreateEndpoint />} />
          <Route path="endpoints/:id" element={<EndpointLayout />}>
            <Route index element={<Navigate to="events" replace />} />
            <Route path="events" element={<EventsTab />} />
            <Route path="security" element={<SecurityTab />} />
            <Route path="delivery" element={<DeliveryTab />} />
            <Route path="logs" element={<LogsTab />} />
            <Route path="settings" element={<SettingsTab />} />
          </Route>
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
