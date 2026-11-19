import { useEffect, useState } from 'react';
import { Link, Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { api, formatDate, NotificationItem, post } from './api';
import { STAFF_ROLES, useAuth } from './auth';
import { resetConnection, useHubEvent } from './realtime';
import LoginPage from './pages/LoginPage';
import { BookingsPage, HistoryPage, ParkingPage, ParkingsPage, PayPage, VehiclesPage } from './pages/DriverPages';
import { AdminParkingsPage, AuditPage, DashboardPage, IncidentsPage, ReportsPage, TariffsPage, UsersPage } from './pages/AdminPages';

export default function App() {
  const { user, loading, logout, hasRole } = useAuth();

  useEffect(() => {
    resetConnection(); // нове з’єднання SignalR з токеном поточного користувача
  }, [user?.id]);

  if (loading) return <div className="center">Завантаження…</div>;
  const isStaff = hasRole(...STAFF_ROLES);

  return (
    <div className="app">
      <header className="topbar">
        <Link to="/" className="brand">🅿 SmartPark</Link>
        <nav>
          <NavLink to="/parkings">Паркінги</NavLink>
          {user && hasRole('Driver', 'Corporate') && (
            <>
              <NavLink to="/vehicles">Мої авто</NavLink>
              <NavLink to="/bookings">Бронювання</NavLink>
              <NavLink to="/history">Історія</NavLink>
            </>
          )}
          {isStaff && (
            <>
              <NavLink to="/admin">Дашборд</NavLink>
              {hasRole('ParkingAdmin', 'SysAdmin') && <NavLink to="/admin/parkings">Керування паркінгами</NavLink>}
              {hasRole('TariffManager', 'SysAdmin') && <NavLink to="/admin/tariffs">Тарифи</NavLink>}
              <NavLink to="/admin/incidents">Інциденти</NavLink>
              <NavLink to="/admin/reports">Звіти</NavLink>
              {hasRole('ParkingAdmin', 'SysAdmin') && <NavLink to="/admin/audit">Історія змін</NavLink>}
              {hasRole('SysAdmin') && <NavLink to="/admin/users">Користувачі</NavLink>}
            </>
          )}
        </nav>
        <div className="userbox">
          {user ? (
            <>
              <Notifications />
              <span title={user.roles.join(', ')}>{user.fullName}</span>
              <button className="link" onClick={logout}>Вийти</button>
            </>
          ) : (
            <Link to="/login">Увійти</Link>
          )}
        </div>
      </header>

      <main className="content">
        <Routes>
          <Route path="/" element={<Navigate to={isStaff ? '/admin' : '/parkings'} />} />
          <Route path="/login" element={user ? <Navigate to="/" /> : <LoginPage />} />
          <Route path="/parkings" element={<ParkingsPage />} />
          <Route path="/parkings/:id" element={<ParkingPage />} />
          <Route path="/pay/:sessionId" element={<PayPage />} />
          <Route path="/vehicles" element={<Protected><VehiclesPage /></Protected>} />
          <Route path="/bookings" element={<Protected><BookingsPage /></Protected>} />
          <Route path="/history" element={<Protected><HistoryPage /></Protected>} />
          <Route path="/admin" element={<Protected staff><DashboardPage /></Protected>} />
          <Route path="/admin/parkings" element={<Protected staff><AdminParkingsPage /></Protected>} />
          <Route path="/admin/tariffs" element={<Protected staff><TariffsPage /></Protected>} />
          <Route path="/admin/incidents" element={<Protected staff><IncidentsPage /></Protected>} />
          <Route path="/admin/reports" element={<Protected staff><ReportsPage /></Protected>} />
          <Route path="/admin/audit" element={<Protected staff><AuditPage /></Protected>} />
          <Route path="/admin/users" element={<Protected staff><UsersPage /></Protected>} />
          <Route path="*" element={<p>Сторінку не знайдено.</p>} />
        </Routes>
      </main>
    </div>
  );
}

function Protected({ children, staff }: { children: JSX.Element; staff?: boolean }) {
  const { user, hasRole } = useAuth();
  if (!user) return <Navigate to="/login" />;
  if (staff && !hasRole(...STAFF_ROLES)) return <p>Недостатньо прав.</p>;
  return children;
}

/** Сповіщення в особистому кабінеті з оновленням у реальному часі. */
function Notifications() {
  const [items, setItems] = useState<NotificationItem[]>([]);
  const [open, setOpen] = useState(false);

  const load = () => api<NotificationItem[]>('/api/notifications').then(setItems).catch(() => undefined);
  useEffect(() => {
    load();
  }, []);
  useHubEvent<NotificationItem>('notification', (n) => setItems((prev) => [{ ...n, isRead: false }, ...prev].slice(0, 50)));

  const unread = items.filter((n) => !n.isRead).length;
  const markRead = async (n: NotificationItem) => {
    await post(`/api/notifications/${n.id}/read`).catch(() => undefined);
    setItems((prev) => prev.map((x) => (x.id === n.id ? { ...x, isRead: true } : x)));
  };

  return (
    <div className="bell">
      <button className="link" onClick={() => setOpen(!open)}>🔔{unread > 0 && <b className="badge">{unread}</b>}</button>
      {open && (
        <div className="dropdown">
          {items.length === 0 && <p className="muted">Сповіщень немає</p>}
          {items.map((n) => (
            <div key={n.id} className={n.isRead ? 'note read' : 'note'} onClick={() => markRead(n)}>
              <strong>{n.title}</strong>
              <div>{n.text}</div>
              <small className="muted">{formatDate(n.createdAt)}</small>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
