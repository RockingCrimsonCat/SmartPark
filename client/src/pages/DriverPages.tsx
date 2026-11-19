import { FormEvent, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { CircleMarker, MapContainer, Popup, TileLayer } from 'react-leaflet';
import { api, del, formatDate, money, ParkingDetails, ParkingSummary, post, put, Spot, Vehicle } from '../api';
import { useAuth } from '../auth';
import { useHubEvent, useWatchParking } from '../realtime';

const SPOT_STATUS: Record<string, string> = {
  Free: 'вільне', Occupied: 'зайняте', Reserved: 'зарезервоване', Blocked: 'заблоковане', Maintenance: 'обслуговування',
};

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

/** Пошук паркінгу: карта Leaflet + OpenStreetMap і список (FR-07). */
export function ParkingsPage() {
  const { data: parkings, error } = useLoad<ParkingSummary[]>('/api/parkings');
  const [filter, setFilter] = useState('');

  if (error) return <p className="error">{error}</p>;
  if (!parkings) return <p>Завантаження…</p>;
  const list = parkings.filter((p) => `${p.name} ${p.city} ${p.address}`.toLowerCase().includes(filter.toLowerCase()));

  return (
    <div className="grid2">
      <div className="card map">
        <MapContainer center={[50.2, 27.5]} zoom={6} style={{ height: '100%' }}>
          <TileLayer attribution="&copy; OpenStreetMap" url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png" />
          {parkings.map((p) => (
            <CircleMarker key={p.id} center={[p.latitude, p.longitude]} radius={10}
              pathOptions={{ color: p.occupancy.free > 0 ? '#1a7f37' : '#cf222e' }}>
              <Popup>
                <b>{p.name}</b><br />Вільно: {p.occupancy.free} з {p.occupancy.total}<br />
                <Link to={`/parkings/${p.id}`}>Детальніше</Link>
              </Popup>
            </CircleMarker>
          ))}
        </MapContainer>
      </div>
      <div className="card">
        <h2>Паркінги</h2>
        <input placeholder="Пошук за назвою чи адресою" value={filter} onChange={(e) => setFilter(e.target.value)} />
        {list.map((p) => (
          <Link key={p.id} to={`/parkings/${p.id}`} className="row-link">
            <div>
              <strong>{p.name}</strong>
              <div className="muted">{p.city}, {p.address} · {p.openingHours}</div>
            </div>
            <span className={p.occupancy.free > 0 ? 'pill ok' : 'pill bad'}>{p.occupancy.free} вільних</span>
          </Link>
        ))}
      </div>
    </div>
  );
}

/** Сторінка паркінгу: «табло», схема місць, шлагбауми (оновлюється в реальному часі). */
export function ParkingPage() {
  const { id } = useParams();
  const { hasRole } = useAuth();
  const { data: p, error, reload, setData } = useLoad<ParkingDetails>(`/api/parkings/${id}`);
  const [barriers, setBarriers] = useState<Record<string, boolean>>({});
  const isStaff = hasRole('Operator', 'ParkingAdmin', 'Technician', 'SysAdmin');

  useWatchParking(id);
  useHubEvent<{ spotId: string; status: string }>('spot', (e) => {
    setData((prev) => prev && {
      ...prev,
      levels: prev.levels.map((l) => ({ ...l, spots: l.spots.map((s) => (s.id === e.spotId ? { ...s, status: e.status } : s)) })),
    });
  });
  useHubEvent<{ parkingId: string; deviceCode: string }>('barrier', (e) => {
    if (e.parkingId !== id) return;
    setBarriers((b) => ({ ...b, [e.deviceCode]: true }));
    setTimeout(() => setBarriers((b) => ({ ...b, [e.deviceCode]: false })), 3000);
  });

  if (error) return <p className="error">{error}</p>;
  if (!p) return <p>Завантаження…</p>;

  const spots = p.levels.flatMap((l) => l.spots);
  const free = spots.filter((s) => s.status === 'Free').length;

  const changeStatus = async (spot: Spot) => {
    const status = prompt('Новий статус: Free, Occupied, Reserved, Blocked, Maintenance', spot.status);
    if (!status) return;
    const reason = ['Blocked', 'Maintenance'].includes(status) ? prompt('Причина') ?? '' : null;
    try {
      await put(`/api/parkings/spots/${spot.id}/status`, { status, reason });
      reload();
    } catch (e) {
      alert((e as Error).message);
    }
  };

  const openBarrier = async (deviceId: string) => {
    const reason = prompt('Причина ручного відкриття');
    if (!reason) return;
    await post(`/api/devices/${deviceId}/open`, { reason }).catch((e) => alert(e.message));
  };

  return (
    <div>
      <div className="card">
        <h2>{p.name}</h2>
        <p className="muted">{p.city}, {p.address} · {p.openingHours}</p>
        <div className="display">Вільних місць: <b>{free}</b> з {spots.length}</div>
        <div className="barriers">
          {p.devices.filter((d) => d.type === 'Barrier').map((d) => (
            <div key={d.id} className={barriers[d.code] ? 'barrier open' : 'barrier'}>
              <span className="arm" /> {d.code} {barriers[d.code] ? '— відкрито' : ''}
              {isStaff && <button className="small" onClick={() => openBarrier(d.id)}>Відкрити</button>}
            </div>
          ))}
        </div>
      </div>
      {p.levels.map((l) => (
        <div key={l.id} className="card">
          <h3>{l.name}</h3>
          <div className="spots">
            {l.spots.map((s) => (
              <button key={s.id} className={`spot ${s.status}`} title={`${SPOT_STATUS[s.status]}${s.statusReason ? ': ' + s.statusReason : ''}`}
                onClick={() => isStaff && changeStatus(s)} disabled={!isStaff}>
                {s.code}
                <small>{s.type === 'Electric' ? '⚡' : s.type === 'Disabled' ? '♿' : ''}</small>
              </button>
            ))}
          </div>
        </div>
      ))}
      <p className="muted">Легенда: зелений – вільне, червоний – зайняте, жовтий – зарезервоване, сірий – недоступне.</p>
    </div>
  );
}

/** Мої авто: додавання, редагування, видалення (UC02) і прив’язка картки (емуляція). */
export function VehiclesPage() {
  const { user, refresh } = useAuth();
  const { data: vehicles, reload } = useLoad<Vehicle[]>('/api/vehicles');
  const [plate, setPlate] = useState('');
  const [brand, setBrand] = useState('');
  const [electric, setElectric] = useState(false);
  const [error, setError] = useState('');

  const add = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      await post('/api/vehicles', { plate, brand, isElectric: electric });
      setPlate('');
      setBrand('');
      reload();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const edit = async (v: Vehicle) => {
    const newBrand = prompt('Марка / модель', v.brand);
    if (newBrand === null) return;
    await put(`/api/vehicles/${v.id}`, { plate: v.plate, brand: newBrand, isElectric: v.isElectric });
    reload();
  };

  const remove = async (v: Vehicle) => {
    if (!confirm(`Видалити авто ${v.plate}?`)) return;
    try {
      await del(`/api/vehicles/${v.id}`);
      reload();
    } catch (err) {
      alert((err as Error).message);
    }
  };

  const toggleCard = async () => {
    await put('/api/auth/payment-method', { linked: !user?.hasPaymentMethod });
    await refresh();
  };

  return (
    <div className="grid2">
      <div className="card">
        <h2>Мої авто</h2>
        <table>
          <thead><tr><th>Номер</th><th>Марка</th><th>EV</th><th /></tr></thead>
          <tbody>
            {vehicles?.map((v) => (
              <tr key={v.id}>
                <td><b>{v.plate}</b></td><td>{v.brand}</td><td>{v.isElectric ? '⚡' : ''}</td>
                <td className="actions">
                  <button className="small" onClick={() => edit(v)}>Змінити</button>
                  <button className="small danger" onClick={() => remove(v)}>Видалити</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="card">
        <form onSubmit={add}>
          <h3>Додати авто</h3>
          <label>Номерний знак<input value={plate} onChange={(e) => setPlate(e.target.value)} placeholder="AA1234BB" required /></label>
          <label>Марка / модель<input value={brand} onChange={(e) => setBrand(e.target.value)} /></label>
          <label className="check"><input type="checkbox" checked={electric} onChange={(e) => setElectric(e.target.checked)} /> Електромобіль</label>
          {error && <p className="error">{error}</p>}
          <button type="submit">Додати</button>
        </form>
        <h3>Спосіб оплати</h3>
        <p>{user?.hasPaymentMethod ? '💳 Картку прив’язано (емуляція) – оплата на виїзді автоматична.' : 'Картку не прив’язано.'}</p>
        <button onClick={toggleCard}>{user?.hasPaymentMethod ? 'Відв’язати картку' : 'Прив’язати картку'}</button>
      </div>
    </div>
  );
}

interface BookingRow { id: string; parking: string; plate: string; startTime: string; endTime: string; status: string; price: number }

/** Бронювання: створення з оплатою, продовження, скасування (UC04). */
export function BookingsPage() {
  const { data: bookings, reload } = useLoad<BookingRow[]>('/api/bookings/my');
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings');
  const { data: vehicles } = useLoad<Vehicle[]>('/api/vehicles');
  const now = new Date();
  const toLocal = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  const [form, setForm] = useState({
    parkingId: '', plate: '', spotType: 'Regular',
    start: toLocal(new Date(now.getTime() + 3600000)), end: toLocal(new Date(now.getTime() + 3 * 3600000)),
  });
  const [quote, setQuote] = useState<number | null>(null);
  const [message, setMessage] = useState('');

  const parkingId = form.parkingId || parkings?.[0]?.id || '';
  const plate = form.plate || vehicles?.[0]?.plate || '';

  useEffect(() => {
    if (!parkingId) return;
    post<{ amount: number }>('/api/tariffs/quote', {
      parkingId, from: new Date(form.start).toISOString(), to: new Date(form.end).toISOString(),
    }).then((q) => setQuote(q.amount)).catch(() => setQuote(null));
  }, [parkingId, form.start, form.end]);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setMessage('');
    try {
      await post('/api/bookings', {
        parkingId, plate, spotType: form.spotType,
        startTime: new Date(form.start).toISOString(), endTime: new Date(form.end).toISOString(),
      });
      setMessage('Бронювання підтверджено ✔');
      reload();
    } catch (err) {
      setMessage((err as Error).message);
    }
  };

  const cancel = async (b: BookingRow) => {
    if (!confirm('Скасувати бронювання?')) return;
    await post(`/api/bookings/${b.id}/cancel`).catch((e) => alert(e.message));
    reload();
  };

  const extend = async (b: BookingRow) => {
    const hours = Number(prompt('Продовжити на годин', '1'));
    if (!hours) return;
    const newEnd = new Date(new Date(b.endTime).getTime() + hours * 3600000).toISOString();
    await post(`/api/bookings/${b.id}/extend`, { newEndTime: newEnd }).catch((e) => alert(e.message));
    reload();
  };

  return (
    <div className="grid2">
      <form className="card" onSubmit={create}>
        <h2>Нове бронювання</h2>
        <label>Паркінг
          <select value={parkingId} onChange={(e) => setForm({ ...form, parkingId: e.target.value })}>
            {parkings?.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.occupancy.free} вільних)</option>)}
          </select>
        </label>
        <label>Авто
          <select value={plate} onChange={(e) => setForm({ ...form, plate: e.target.value })}>
            {vehicles?.map((v) => <option key={v.id} value={v.plate}>{v.plate} {v.brand}</option>)}
          </select>
        </label>
        <label>Тип місця
          <select value={form.spotType} onChange={(e) => setForm({ ...form, spotType: e.target.value })}>
            <option value="Regular">Звичайне</option>
            <option value="Electric">Із зарядкою</option>
            <option value="Disabled">Для людей з інвалідністю</option>
          </select>
        </label>
        <label>Початок<input type="datetime-local" value={form.start} onChange={(e) => setForm({ ...form, start: e.target.value })} /></label>
        <label>Кінець<input type="datetime-local" value={form.end} onChange={(e) => setForm({ ...form, end: e.target.value })} /></label>
        <p>Вартість: <b>{quote !== null ? money(quote + 10) : '—'}</b> <span className="muted">(з сервісним збором 10 грн)</span></p>
        {message && <p className={message.includes('✔') ? 'ok-text' : 'error'}>{message}</p>}
        <button type="submit" disabled={!vehicles?.length}>Забронювати й оплатити</button>
        {!vehicles?.length && <p className="muted">Спершу додайте авто.</p>}
      </form>
      <div className="card">
        <h2>Мої бронювання</h2>
        <table>
          <thead><tr><th>Паркінг</th><th>Авто</th><th>Час</th><th>Статус</th><th>Сума</th><th /></tr></thead>
          <tbody>
            {bookings?.map((b) => (
              <tr key={b.id}>
                <td>{b.parking}</td><td>{b.plate}</td>
                <td>{formatDate(b.startTime)} – {formatDate(b.endTime)}</td>
                <td>{b.status}</td><td>{money(b.price)}</td>
                <td className="actions">
                  {['Confirmed', 'Activated'].includes(b.status) && <button className="small" onClick={() => extend(b)}>Продовжити</button>}
                  {b.status === 'Confirmed' && <button className="small danger" onClick={() => cancel(b)}>Скасувати</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

interface SessionRow { id: string; plate: string; parking: string; entryTime: string; exitTime?: string; status: string; mode: string; amount: number; paymentId?: string }
interface SubscriptionRow { id: string; plate: string; parkingId: string; validFrom: string; validTo: string; price: number }

/** Історія паркувань і платежів, чеки, абонементи (UC09). */
export function HistoryPage() {
  const { data: sessions } = useLoad<SessionRow[]>('/api/sessions/my');
  const { data: subs, reload } = useLoad<SubscriptionRow[]>('/api/subscriptions/my');
  const { data: parkings } = useLoad<ParkingSummary[]>('/api/parkings');
  const { data: vehicles } = useLoad<Vehicle[]>('/api/vehicles');
  const [buy, setBuy] = useState({ parkingId: '', plate: '', months: 1 });

  const buySubscription = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await post('/api/subscriptions', {
        parkingId: buy.parkingId || parkings?.[0]?.id, plate: buy.plate || vehicles?.[0]?.plate, months: buy.months,
      });
      reload();
    } catch (err) {
      alert((err as Error).message);
    }
  };

  return (
    <div>
      <div className="card">
        <h2>Історія паркувань</h2>
        <table>
          <thead><tr><th>Паркінг</th><th>Авто</th><th>В’їзд</th><th>Виїзд</th><th>Режим</th><th>Статус</th><th>Сума</th><th>Чек</th></tr></thead>
          <tbody>
            {sessions?.map((s) => (
              <tr key={s.id}>
                <td>{s.parking}</td><td>{s.plate}</td><td>{formatDate(s.entryTime)}</td><td>{formatDate(s.exitTime)}</td>
                <td>{s.mode}</td><td>{s.status}</td><td>{money(s.amount)}</td>
                <td>{s.paymentId && <a href={`/api/payments/${s.paymentId}/receipt`} target="_blank">PDF</a>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="grid2">
        <div className="card">
          <h2>Мої абонементи</h2>
          {subs?.map((s) => (
            <p key={s.id}><b>{s.plate}</b> – {parkings?.find((p) => p.id === s.parkingId)?.name}, до {formatDate(s.validTo)} ({money(s.price)})</p>
          ))}
        </div>
        <form className="card" onSubmit={buySubscription}>
          <h3>Купити абонемент</h3>
          <select onChange={(e) => setBuy({ ...buy, parkingId: e.target.value })}>
            {parkings?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <select onChange={(e) => setBuy({ ...buy, plate: e.target.value })}>
            {vehicles?.map((v) => <option key={v.id} value={v.plate}>{v.plate}</option>)}
          </select>
          <label>Місяців<input type="number" min={1} max={12} value={buy.months} onChange={(e) => setBuy({ ...buy, months: Number(e.target.value) })} /></label>
          <button type="submit">Оплатити</button>
        </form>
      </div>
    </div>
  );
}

interface PayInfo { id: string; plate: string; parking: string; entryTime: string; status: string; paidAt?: string; amount: number }

/** Оплата гостем за QR-кодом із табло (без реєстрації) – емуляція паркомата. */
export function PayPage() {
  const { sessionId } = useParams();
  const { data, error, reload } = useLoad<PayInfo>(`/api/payments/sessions/${sessionId}`);
  const [result, setResult] = useState('');

  const pay = async () => {
    try {
      const r = await post<{ status: string; failureReason?: string; id: string }>(`/api/payments/sessions/${sessionId}`);
      setResult(r.status === 'Succeeded' ? `Оплачено ✔ Чек: /api/payments/${r.id}/receipt. Маєте 15 хв на виїзд.` : `Відмова: ${r.failureReason}`);
      reload();
    } catch (e) {
      setResult((e as Error).message);
    }
  };

  if (error) return <p className="error">{error}</p>;
  if (!data) return <p>Завантаження…</p>;
  return (
    <div className="card narrow">
      <h2>Оплата паркування</h2>
      <p>Паркінг: <b>{data.parking}</b></p>
      <p>Авто: <b>{data.plate}</b>, в’їзд {formatDate(data.entryTime)}</p>
      <p className="big">До сплати: {money(data.amount)}</p>
      {data.paidAt ? <p className="ok-text">Оплачено {formatDate(data.paidAt)}</p> : <button onClick={pay}>Оплатити</button>}
      {result && <p>{result}</p>}
    </div>
  );
}
