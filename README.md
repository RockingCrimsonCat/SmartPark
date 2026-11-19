# SmartPark – типова інформаційна система мережі інтелектуальних паркінгів

Прототип (MVP): модульний моноліт ASP.NET Core 8 + PostgreSQL 16 + React, симулятор обладнання паркінгу.

## Запуск

Потрібен Docker Desktop (або Docker Engine 24+).

```bash
docker compose up -d --build
```

- Веб-клієнт: http://localhost:8080
- Swagger (REST API): http://localhost:5000/swagger
- PostgreSQL: localhost:5432 (база/користувач/пароль `smartpark`)

Порти прив’язані до `127.0.0.1`: система доступна лише з комп’ютера, на якому запущена, і не видна з локальної мережі.

Симулятор паркінгу:

```bash
# інтерактивний режим (клавіші 1–6, o – обрив зв’язку, s – стан, q – вихід)
docker compose --profile auto-sim run --rm simulator

# автоматичний режим – випадкова подія кожні 5 с
docker compose --profile auto-sim run --rm simulator --auto

# сценарій для демонстрації: команди через «;», пауза 3 с
docker compose --profile auto-sim run --rm simulator --script "1:AA1234BB;3;o;1:KA0001EV;o;4:AA1234BB;s"
```

## Тестові облікові записи (лише для середовища розробки)

Пароль для всіх: `Test#12345`

| E-mail | Роль |
|---|---|
| admin@smartpark.test | SysAdmin |
| operator@smartpark.test | Operator |
| parkadmin@smartpark.test | ParkingAdmin |
| tech@smartpark.test | Technician |
| tariff@smartpark.test | TariffManager |
| finance@smartpark.test | Finance |
| driver@smartpark.test | Driver (авто AA1234BB, KA0001EV з абонементом) |

Номер `BX6666XX` – у чорному списку.

## Розробка без Docker

```bash
dotnet build SmartPark.sln
dotnet test SmartPark.sln
dotnet run --project src/SmartPark.Server          # потрібен PostgreSQL на localhost:5432
cd client && npm install && npm run dev            # http://localhost:5173
```

## Структура

- `src/SmartPark.Contracts` – контракти edge-API та інтеграційні події
- `src/SmartPark.Data` – моделі, AppDbContext, міграції, аудит
- `src/SmartPark.Server` – модулі (Identity, Catalog, Booking, Session, Pricing, Payment, Device, Incident, Notification, Reporting), шина подій, SignalR
- `src/SmartPark.Simulator` – симулятор edge-шлюзу та обладнання
- `tests/SmartPark.Tests` – модульні тести (xUnit)
- `client` – React + TypeScript (Vite)

## Примітка щодо Docker Desktop на Windows

Якщо Docker Desktop падає з помилкою `remove ...\dockerInference` або `...\docker-secrets-engine\engine.sock: The file cannot be accessed by the system`,
перед запуском перейменуйте папки `%LOCALAPPDATA%\Docker\run` і `%LOCALAPPDATA%\docker-secrets-engine` (Docker створить їх заново).
