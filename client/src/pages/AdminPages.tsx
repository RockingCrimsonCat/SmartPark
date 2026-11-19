import { FormEvent, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, del, formatDate, money, ParkingSummary, post, put, Tariff } from '../api';
import { useAuth } from '../auth';
import { useHubEvent } from '../realtime';

function useLoad<T>(url: string | null, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState('');
  const reload = () => {
    if (!url) return;
    api<T>(url).then(setData).catch((e) => setError(e.message));
  };
  useEffect(reload, [url, ...deps]);
  return { data, error, reload, setData };
}

interface Kpi { activeSessions: number; sessionsToday: number; revenueToday: number; openIncidents: number; faultyDevices: number; unpaidSessions: number }
interface EventRow { type: string; payload: string; occurredAt: string; processedAt?: string }
interface OccupancyRow { parking: string; total: number; occupied: number; free: number; occupancyPercent: number }

/** Дашборд ситуаційного центру: KPI, зайнятість, журнал подій шини, ручне підтвердження в’їзду (UC11). */
export function DashboardPage() {
  const { data: kpi, reload: reloadKpi } = useLoad<Kpi>('/api/reports/kpi');
  const { data: occupancy, reload: reloadOcc } = useLoad<OccupancyRow[]>('/api/reports/occupancy');
  const { data: initialEvents } = useLoad<EventRow[]>('/api/reports/events');
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings');
  const [events, setEvents] = useState<EventRow[]>([]);
  const [manual, setManual] = useState({ parkingId: '', plate: '', reason: '' });
  const [manualResult, setManualResult] = useState('');

  useEffect(() => {
    if (initialEvents) setEvents(initialEvents);
  }, [initialEvents]);

  useHubEvent<EventRow>('event', (e) => {
    setEvents((prev) => [{ ...e, processedAt: new Date().toISOString() }, ...prev].slice(0, 100));
    reloadKpi();
    if (e.type === 'SpotStatusChanged' || e.type.startsWith('Session')) reloadOcc();
  });

  const manualEntry = async (e: FormEvent) => {
    e.preventDefault();
    try {
      const r = await post<{ decision: string; message: string }>('/api/sessions/manual-entry', {
        parkingId: manual.parkingId || parkings?.[0]?.id, plate: manual.plate, laneCode: 'BAR-IN-1', reason: manual.reason,
      });
      setManualResult(`${r.decision}: ${r.message}`);
    } catch (err) {
      setManualResult((err as Error).message);
    }
  };

  return (
    <div>
      <div className="kpis">
        <Kpi label="Активні сесії" value={kpi?.activeSessions} />
        <Kpi label="В’їздів сьогодні" value={kpi?.sessionsToday} />
        <Kpi label="Дохід сьогодні" value={kpi ? money(kpi.revenueToday) : undefined} />
        <Kpi label="Відкриті інциденти" value={kpi?.openIncidents} warn={!!kpi?.openIncidents} />
        <Kpi label="Несправні пристрої" value={kpi?.faultyDevices} warn={!!kpi?.faultyDevices} />
        <Kpi label="Неоплачені сесії" value={kpi?.unpaidSessions} warn={!!kpi?.unpaidSessions} />
      </div>
      <div className="grid2">
        <div className="card">
          <h2>Зайнятість паркінгів</h2>
          <table>
            <thead><tr><th>Паркінг</th><th>Зайнято</th><th>Вільно</th><th>%</th></tr></thead>
            <tbody>
              {occupancy?.map((o) => (
                <tr key={o.parking}>
                  <td>{o.parking}</td><td>{o.occupied}</td><td>{o.free}</td>
                  <td><div className="bar"><span style={{ width: `${o.occupancyPercent}%` }} /></div>{o.occupancyPercent}%</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p>{parkings?.map((p) => <Link key={p.id} to={`/parkings/${p.id}`} className="chip">{p.name}</Link>)}</p>
          <form onSubmit={manualEntry}>
            <h3>Ручне підтвердження в’їзду</h3>
            <select onChange={(e) => setManual({ ...manual, parkingId: e.target.value })}>
              {parkings?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
            <input placeholder="Номер авто" value={manual.plate} onChange={(e) => setManual({ ...manual, plate: e.target.value })} required />
            <input placeholder="Причина (обов’язково)" value={manual.reason} onChange={(e) => setManual({ ...manual, reason: e.target.value })} required />
            <button type="submit">Впустити й відкрити шлагбаум</button>
            {manualResult && <p>{manualResult}</p>}
          </form>
        </div>
        <div className="card">
          <h2>Журнал подій (реальний час)</h2>
          <div className="log">
            {events.map((e, i) => (
              <div key={i} className="log-row">
                <small className="muted">{formatDate(e.occurredAt)}</small> <b>{e.type}</b>
                <code>{e.payload}</code>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function Kpi({ label, value, warn }: { label: string; value?: number | string; warn?: boolean }) {
  return <div className={warn ? 'kpi warn' : 'kpi'}><span>{label}</span><b>{value ?? '—'}</b></div>;
}

/** Керування паркінгами: додавання, редагування, рівні, активація, архівування (UC14). */
export function AdminParkingsPage() {
  const { data: parkings, reload } = useLoad<ParkingSummary[]>('/api/parkings?includeInactive=true');
  const empty = { id: '', name: '', city: 'Київ', address: '', latitude: 50.45, longitude: 30.52, openingHours: '24/7' };
  const [form, setForm] = useState(empty);
  const [message, setMessage] = useState('');

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setMessage('');
    try {
      if (form.id) await put(`/api/parkings/${form.id}`, form);
      else await post('/api/parkings', form);
      setForm(empty);
      reload();
    } catch (err) {
      setMessage((err as Error).message);
    }
  };

  const action = async (fn: () => Promise<unknown>) => {
    setMessage('');
    try {
      await fn();
      reload();
    } catch (err) {
      setMessage((err as Error).message);
    }
  };

  const addLevel = (p: ParkingSummary) => {
    const number = Number(prompt('Номер рівня', '1'));
    const regular = Number(prompt('Кількість звичайних місць', '10'));
    const electric = Number(prompt('Місць із зарядкою', '2'));
    if (!number) return;
    action(() => post(`/api/parkings/${p.id}/levels`, {
      name: `Рівень ${number}`, number, regularSpots: regular, electricSpots: electric, disabledSpots: 1,
    }));
  };

  const addBarrier = (p: ParkingSummary) =>
    action(async () => {
      await post('/api/devices', { parkingId: p.id, code: 'BAR-IN-1', type: 'Barrier' });
      await post('/api/devices', { parkingId: p.id, code: 'BAR-OUT-1', type: 'Barrier' });
    });

  return (
    <div className="grid2">
      <div className="card">
        <h2>Паркінги мережі</h2>
        {message && <p className="error">{message}</p>}
        <table>
          <thead><tr><th>Назва</th><th>Місто</th><th>Статус</th><th>Місць</th><th /></tr></thead>
          <tbody>
            {parkings?.map((p) => (
              <tr key={p.id}>
                <td><Link to={`/parkings/${p.id}`}>{p.name}</Link></td><td>{p.city}</td><td>{p.status}</td><td>{p.occupancy.total}</td>
                <td className="actions">
                  <button className="small" onClick={() => setForm({ ...p })}>Змінити</button>
                  <button className="small" onClick={() => addLevel(p)}>+ Рівень</button>
                  {p.status === 'Draft' && <button className="small" onClick={() => addBarrier(p)}>+ Шлагбауми</button>}
                  {p.status === 'Draft' && <button className="small" onClick={() => action(() => post(`/api/parkings/${p.id}/activate`))}>Активувати</button>}
                  {p.status !== 'Archived' && <button className="small danger" onClick={() => confirm('Архівувати паркінг?') && action(() => del(`/api/parkings/${p.id}`))}>Архівувати</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="muted">Після створення: додайте рівні, шлагбауми та тариф (сторінка «Тарифи»), потім активуйте паркінг.</p>
      </div>
      <form className="card" onSubmit={save}>
        <h2>{form.id ? 'Редагування паркінгу' : 'Новий паркінг'}</h2>
        <label>Назва<input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required /></label>
        <label>Місто<input value={form.city} onChange={(e) => setForm({ ...form, city: e.target.value })} required /></label>
        <label>Адреса<input value={form.address} onChange={(e) => setForm({ ...form, address: e.target.value })} required /></label>
        <label>Широта<input type="number" step="0.0001" value={form.latitude} onChange={(e) => setForm({ ...form, latitude: Number(e.target.value) })} /></label>
        <label>Довгота<input type="number" step="0.0001" value={form.longitude} onChange={(e) => setForm({ ...form, longitude: Number(e.target.value) })} /></label>
        <label>Графік роботи<input value={form.openingHours} onChange={(e) => setForm({ ...form, openingHours: e.target.value })} /></label>
        <button type="submit">Зберегти</button>
        {form.id && <button type="button" className="link" onClick={() => setForm(empty)}>Скасувати</button>}
      </form>
    </div>
  );
}

/** Тарифи з версіями (UC15): нова версія діє з указаної дати, попередні зберігаються. */
export function TariffsPage() {
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings?includeInactive=true');
  const [parkingId, setParkingId] = useState('');
  const pid = parkingId || parkings?.[0]?.id || '';
  const { data: tariffs, reload } = useLoad<Tariff[]>(pid ? `/api/tariffs?parkingId=${pid}` : null, [pid]);
  const [v, setV] = useState({ dayRate: 40, nightRate: 20, nightStartHour: 22, nightEndHour: 7, freeMinutes: 15, dailyMax: 400, subscriptionMonthPrice: 2500, validFrom: '' });
  const [message, setMessage] = useState('');

  const addVersion = async (e: FormEvent) => {
    e.preventDefault();
    setMessage('');
    const version = { ...v, validFrom: new Date(v.validFrom || Date.now()).toISOString() };
    try {
      if (tariffs && tariffs.length > 0) await post(`/api/tariffs/${tariffs[0].id}/versions`, version);
      else await post('/api/tariffs', { parkingId: pid, name: 'Базовий', firstVersion: version });
      setMessage('Збережено ✔');
      reload();
    } catch (err) {
      setMessage((err as Error).message);
    }
  };

  const field = (key: keyof typeof v, label: string) => (
    <label>{label}<input type="number" value={v[key] as number} onChange={(e) => setV({ ...v, [key]: Number(e.target.value) })} /></label>
  );

  return (
    <div className="grid2">
      <div className="card">
        <h2>Тарифи</h2>
        <select value={pid} onChange={(e) => setParkingId(e.target.value)}>
          {parkings?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        {tariffs?.map((t) => (
          <div key={t.id}>
            <h3>{t.name} {t.isArchived && '(архів)'}</h3>
            <table>
              <thead><tr><th>Діє з</th><th>День</th><th>Ніч</th><th>Ніч (год)</th><th>Безкошт., хв</th><th>Макс./доба</th><th>Абонемент</th><th>Автор</th></tr></thead>
              <tbody>
                {t.versions.map((x) => (
                  <tr key={x.id}>
                    <td>{formatDate(x.validFrom)}</td><td>{x.dayRate}</td><td>{x.nightRate}</td><td>{x.nightStartHour}–{x.nightEndHour}</td>
                    <td>{x.freeMinutes}</td><td>{x.dailyMax}</td><td>{x.subscriptionMonthPrice}</td><td>{x.createdBy}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
      </div>
      <form className="card" onSubmit={addVersion}>
        <h3>Нова версія тарифу</h3>
        <label>Діє з<input type="datetime-local" value={v.validFrom} onChange={(e) => setV({ ...v, validFrom: e.target.value })} /></label>
        {field('dayRate', 'Денна ставка, грн/год')}
        {field('nightRate', 'Нічна ставка, грн/год')}
        {field('nightStartHour', 'Початок ночі (год)')}
        {field('nightEndHour', 'Кінець ночі (год)')}
        {field('freeMinutes', 'Безкоштовні хвилини')}
        {field('dailyMax', 'Максимум за добу, грн')}
        {field('subscriptionMonthPrice', 'Абонемент, грн/міс')}
        {message && <p>{message}</p>}
        <button type="submit">Зберегти версію</button>
      </form>
    </div>
  );
}

interface IncidentRow { id: string; title: string; description: string; priority: string; status: string; createdAt: string; parkingId: string; assigneeId?: string }
interface UserRow { id: string; email: string; fullName: string; roles: string[]; isBlocked: boolean; parkingIds: string[] }

/** Інциденти та заявки технікам (UC12): статуси, призначення, коментарі. */
export function IncidentsPage() {
  const { hasRole } = useAuth();
  const { data: incidents, reload } = useLoad<IncidentRow[]>('/api/incidents');
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings');
  const [form, setForm] = useState({ title: '', description: '', priority: 'Medium', parkingId: '' });

  useHubEvent('notification', reload);

  const setStatus = async (i: IncidentRow, status: string) => {
    const comment = prompt('Коментар (необов’язково)') ?? '';
    await put(`/api/incidents/${i.id}/status`, { status, comment }).catch((e) => alert(e.message));
    reload();
  };

  const create = async (e: FormEvent) => {
    e.preventDefault();
    await post('/api/incidents', { ...form, parkingId: form.parkingId || parkings?.[0]?.id, deviceId: null });
    setForm({ ...form, title: '', description: '' });
    reload();
  };

  const next: Record<string, string[]> = { New: ['InProgress', 'Closed'], InProgress: ['Resolved'], Resolved: ['Closed', 'InProgress'], Closed: [] };

  return (
    <div className="grid2">
      <div className="card">
        <h2>Інциденти</h2>
        <table>
          <thead><tr><th>Створено</th><th>Опис</th><th>Пріоритет</th><th>Статус</th><th /></tr></thead>
          <tbody>
            {incidents?.map((i) => (
              <tr key={i.id} className={i.status === 'Closed' ? 'muted' : ''}>
                <td>{formatDate(i.createdAt)}</td>
                <td><b>{i.title}</b><div className="muted">{i.description}</div></td>
                <td>{i.priority}</td><td>{i.status}</td>
                <td className="actions">
                  {next[i.status].map((s) => <button key={s} className="small" onClick={() => setStatus(i, s)}>{s}</button>)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {hasRole('Operator', 'ParkingAdmin', 'SysAdmin', 'Technician') && (
        <form className="card" onSubmit={create}>
          <h3>Зареєструвати інцидент</h3>
          <select onChange={(e) => setForm({ ...form, parkingId: e.target.value })}>
            {parkings?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <input placeholder="Заголовок" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} required />
          <textarea placeholder="Опис" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          <select value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })}>
            {['Low', 'Medium', 'High', 'Critical'].map((p) => <option key={p}>{p}</option>)}
          </select>
          <button type="submit">Створити</button>
        </form>
      )}
    </div>
  );
}

interface RevenueRow { day: string; parking: string; sessions: number; revenue: number; avgMinutes: number }

/** Звіти (SQL-представлення reporting.*). */
export function ReportsPage() {
  const { data: rows } = useLoad<RevenueRow[]>('/api/reports/revenue');
  const max = Math.max(1, ...(rows ?? []).map((r) => r.revenue));
  const total = (rows ?? []).reduce((s, r) => s + r.revenue, 0);
  return (
    <div className="card">
      <h2>Дохід і сесії за 30 днів</h2>
      <p>Разом: <b>{money(total)}</b></p>
      <table>
        <thead><tr><th>День</th><th>Паркінг</th><th>Сесій</th><th>Середня тривалість</th><th>Дохід</th><th /></tr></thead>
        <tbody>
          {rows?.map((r, i) => (
            <tr key={i}>
              <td>{new Date(r.day).toLocaleDateString('uk-UA')}</td><td>{r.parking}</td><td>{r.sessions}</td>
              <td>{Math.round(r.avgMinutes)} хв</td><td>{money(r.revenue)}</td>
              <td><div className="bar"><span style={{ width: `${(r.revenue / max) * 100}%` }} /></div></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface AuditRow { id: number; entityName: string; entityId: string; action: string; oldValues?: string; newValues?: string; userName?: string; timestamp: string }

/** Історія змін (UC18): незмінний журнал аудиту з порівнянням значень. */
export function AuditPage() {
  const [filter, setFilter] = useState({ entityName: '', userName: '' });
  const query = new URLSearchParams(Object.entries(filter).filter(([, v]) => v)).toString();
  const { data: rows } = useLoad<AuditRow[]>(`/api/reports/audit?${query}`, [query]);

  const diff = (r: AuditRow) => {
    const oldV = r.oldValues ? JSON.parse(r.oldValues) : {};
    const newV = r.newValues ? JSON.parse(r.newValues) : {};
    const keys = Array.from(new Set([...Object.keys(oldV), ...Object.keys(newV)]));
    return keys.map((k) => (
      <div key={k}><b>{k}</b>: {r.action !== 'Created' && <del>{String(oldV[k] ?? '')}</del>} {r.action !== 'Deleted' && <ins>{String(newV[k] ?? '')}</ins>}</div>
    ));
  };

  return (
    <div className="card">
      <h2>Історія змін</h2>
      <div className="filters">
        <select value={filter.entityName} onChange={(e) => setFilter({ ...filter, entityName: e.target.value })}>
          <option value="">Усі сутності</option>
          {['Parking', 'Spot', 'Tariff', 'TariffVersion', 'Vehicle', 'Booking', 'Device', 'Incident', 'Subscription', 'Refund'].map((n) => <option key={n}>{n}</option>)}
        </select>
        <input placeholder="Користувач" value={filter.userName} onChange={(e) => setFilter({ ...filter, userName: e.target.value })} />
      </div>
      <table>
        <thead><tr><th>Час</th><th>Користувач</th><th>Дія</th><th>Сутність</th><th>Зміни</th></tr></thead>
        <tbody>
          {rows?.map((r) => (
            <tr key={r.id}>
              <td>{formatDate(r.timestamp)}</td><td>{r.userName}</td><td>{r.action}</td>
              <td>{r.entityName}<div className="muted small-text">{r.entityId}</div></td>
              <td className="diff">{diff(r)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const ALL_ROLES = ['Driver', 'Corporate', 'Operator', 'ParkingAdmin', 'Technician', 'TariffManager', 'Finance', 'SysAdmin'];

/** Керування користувачами та ролями (UC17). */
export function UsersPage() {
  const { data: users, reload } = useLoad<UserRow[]>('/api/users');
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings?includeInactive=true');
  const [form, setForm] = useState({ email: '', fullName: '', temporaryPassword: '', roles: ['Operator'] as string[], parkingIds: [] as string[] });
  const [message, setMessage] = useState('');

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setMessage('');
    try {
      await post('/api/users', form);
      setForm({ ...form, email: '', fullName: '', temporaryPassword: '' });
      reload();
    } catch (err) {
      setMessage((err as Error).message);
    }
  };

  const editRoles = async (u: UserRow) => {
    const roles = prompt(`Ролі через кому (${ALL_ROLES.join(', ')})`, u.roles.join(','));
    if (roles === null) return;
    await put(`/api/users/${u.id}`, { fullName: u.fullName, roles: roles.split(',').map((r) => r.trim()).filter(Boolean), parkingIds: u.parkingIds })
      .catch((e) => alert(e.message));
    reload();
  };

  const toggle = <T,>(list: T[], item: T) => (list.includes(item) ? list.filter((x) => x !== item) : [...list, item]);

  return (
    <div className="grid2">
      <div className="card">
        <h2>Користувачі</h2>
        <table>
          <thead><tr><th>E-mail</th><th>ПІБ</th><th>Ролі</th><th>Стан</th><th /></tr></thead>
          <tbody>
            {users?.map((u) => (
              <tr key={u.id} className={u.isBlocked ? 'muted' : ''}>
                <td>{u.email}</td><td>{u.fullName}</td><td>{u.roles.join(', ')}</td><td>{u.isBlocked ? 'заблоковано' : 'активний'}</td>
                <td className="actions">
                  <button className="small" onClick={() => editRoles(u)}>Ролі</button>
                  <button className="small danger" onClick={() => post(`/api/users/${u.id}/block?blocked=${!u.isBlocked}`).then(reload).catch((e) => alert(e.message))}>
                    {u.isBlocked ? 'Розблокувати' : 'Заблокувати'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <form className="card" onSubmit={create}>
        <h3>Новий працівник</h3>
        <label>E-mail<input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required /></label>
        <label>ПІБ<input value={form.fullName} onChange={(e) => setForm({ ...form, fullName: e.target.value })} required /></label>
        <label>Тимчасовий пароль<input value={form.temporaryPassword} onChange={(e) => setForm({ ...form, temporaryPassword: e.target.value })} required minLength={8} /></label>
        <fieldset><legend>Ролі</legend>
          {ALL_ROLES.map((r) => (
            <label key={r} className="check"><input type="checkbox" checked={form.roles.includes(r)} onChange={() => setForm({ ...form, roles: toggle(form.roles, r) })} /> {r}</label>
          ))}
        </fieldset>
        <fieldset><legend>Паркінги</legend>
          {parkings?.map((p) => (
            <label key={p.id} className="check"><input type="checkbox" checked={form.parkingIds.includes(p.id)} onChange={() => setForm({ ...form, parkingIds: toggle(form.parkingIds, p.id) })} /> {p.name}</label>
          ))}
        </fieldset>
        {message && <p className="error">{message}</p>}
        <button type="submit">Створити</button>
      </form>
    </div>
  );
}
