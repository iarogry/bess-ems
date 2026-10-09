# Поточне завдання: BESS EMS як продукт із керованим агентом

## Рішення користувача

Єдиний шлюз запису має працювати **на окремому сервері застосунку**.
Це рішення про майбутню архітектуру, не дозвіл перемикати чинний runner,
змінювати його облікові дані, запускати міграції production або писати в Deye.

## Незмінна умова

Зберегти усталену роботу. Розробка відбувається в ізольованому worktree
`agent-productization-worktree`, гілка `codex/agent-productization`.
Спочатку read-only/shadow, потім підтверджена еквівалентність, окреме погодження
пілота і лише тоді контрольоване перенесення права запису. Новий агент не має
отримувати прямі vendor-credentials або довільний інструмент HTTP-запису.

## Поточний етап

### Оновлення 8 жовтня 2026: добовий цикл EMS

Реалізовано запуск EMS о 15:00 Europe/Kyiv, ENTSO-E + свіжа telemetry,
фізичну battery model, immutable day plan та компілятори API. Deye формує
Z1–Z4; tracking Modbus/MQTT/OPC UA зберігає повний розклад без цих обмежень.
Фізичні моделі та API обираються незалежно. Інші physical models (PV,
генератори, навантаження) та спільний оптимізатор площадки ще не реалізовані.

Додано окремий Deye day authorization: після незалежного approval до Z1
scheduler може активувати чотири вікна через HTTPS broker без ручної pilot
session для кожного. Нові claims зберігаються в PostgreSQL; broker повторно
перевіряє day scope, revocation, safety і lease на mutation boundary.
Unknown та replay не дозволяють повторний фізичний запис. Добовий режим
вимкнений за замовчуванням і потребує opt-in окремо в EMS та broker.
Локальна перевірка: 885/885 tests passed, включно з повним PostgreSQL
persistence/broker suite на схемі 0023. Деталі:
[ems-day-ahead-planning.md](ems-day-ahead-planning.md).

Це реалізація в поточному cloud checkout, не deployment. Старий writer і
зовнішню автоматизацію 15:00 не змінювали; live shadow/sole-writer proof
на сервері не отримано. У середовищі немає VPN/server secrets/vendor egress.

### Актуалізація 7 жовтня 2026

У поточному cloud checkout виправлено strict Persistence integration build
без послаблення аналізаторів: 108/108 tests passed на власній PostgreSQL 16.15.
Додано й перевірено чотири синтетичні HTTPS BrokerHost SIGKILL/DB-crash
сценарії: Initiated зберігається, повторний запис заблокований. Dashboard
отримав generation/abort guards, unavailable/partial states і freshness;
8/8 behavior tests passed. Див. [recovery-verification.md](recovery-verification.md).

Відновлено історію **окремого застосункового staging**, не BrokerHost pilot:
сервер `10.10.70.66`, Debian 12 x86_64; read-only gateway на port 3000,
останній recorded image `bess-ems-staging:5ec99720-fix1`. Код deployed image
відрізняється від цього cloud baseline `7c59d1a`; перед оновленням потрібно
звірити source/image і зберегти існуючу БД. Серверні перевірки з попереднього
чату є історичними, не новим live probe. Hardware writes там вимкнені.

Підготовлено [private TLS/monitoring package](../../deploy/staging/README.md)
та [live evidence checklist](pilot-evidence-checklist.md). Поточна cloud-сесія
не має VPN/TCP route і server identity до цього сервера. Remote TLS/monitor
не встановлювалися; реальні shadow та physical writer boundary не підтверджені.
Наступний крок — доступ через серверне/VPN середовище, reconcile source,
перевірка/встановлення TLS і monitoring, read-only збір live evidence.
Broker/device enablement і hardware pilot залишаються окремими gates.

Підготувати серверне компонування broker-а без увімкнення запису:

- Окреме отримання vendor-токена до створення durable attempt; жодного
  auth-handler-а, який повторює mutation після 401.
- Незмінний токен і окремий HTTP-клієнт на одну виконувану спробу.
- Явна server-side прив'язка site/station/master/slave та вимкнений типово драйвер.
- Перевірки відмов авторизації до admission і відсутності повторного запису.
- Пакет staging із закритим типово запуском, реальним TLS і приватним доступом.

Провайдер токена та його підключення до окремого broker host реалізовані.
Фабрика готує токен до admission і створює окремий disposable клієнт/драйвер
на одну спробу. `Broker:Deye:Enabled` і `Broker:Deye:WriteEnabled` типово false;
обидва потрібні для реального драйвера. Типовий host продовжує відмовляти
в записі. Новий провайдер не є DelegatingHandler і сам не виконує команд пристрою.
Це реалізація можливості, а не погодження її ввімкнути.

Серверний BrokerHost-пакет зібраний із чистого commit `e1933a2f`; хеші та
результати двох локальних disabled-запусків записані в current-baseline.
Сценарії пакування й перевірки та [runbook](broker-staging-runbook.md) готові.
Це ще не remote staging: адресу/ОС/TLS/сховище секретів не обрано.
Наступні програмні кроки — bounded клієнти та enabled-process recovery tests
на власній тестовій БД; серверний запуск і будь-який hardware pilot окремі.

## Що лишається перед готовністю продукту

- Реальні shadow-порівняння: штучні тестові відповіді не доводять еквівалентності.
- Довірене server-side підтвердження legacy-сценарію; зараз legacy resolver відмовляє.
- Перевірка протоколу/метрик на реальній станції спочатку лише читанням.
- Фізичне обмеження credentials/egress: старий прямий шлях не може обходити broker.
- Реальний TLS, синхронізація часу, перезапуск та відновлення на staging.
- Контрольований пілот, kill switch, rollback і процедура reconciliation Unknown.
- Повний release/CI: раніше зафіксовані Optimization analyzer findings ще відкриті.

Unknown не звільняється автоматично: втрачена відповідь не доводить, що запису
не було. Rollback або закінчення lease також не дозволяють повторити команду.

## Безпечна підготовка staging

До запуску потрібні адреса/ОС окремого сервера, приватна мережа, TLS-сертифікат,
окрема тестова БД та сховище секретів. Ці параметри ще не зафіксовані.
Не переносити production connection string або operational secrets у приклад конфігурації.

Перший запуск має бути локальним на staging з явним `Broker:Enabled=false`:
жодного DB admission, міграції чи vendor-виклику. `/healthz` має повертати 503,
а `/v1/device-writes` не повинен мати маршруту. Перевіряти це локально на сервері,
не відкриваючи HTTP-порт назовні. Службу автозапуску поки не встановлювати.

Увімкнення `Broker:Enabled=true` вже запускає міграцію налаштованої БД.
Тому це окремий погоджуваний крок лише на спеціальній staging БД; health 503
не означає відсутності міграції. Без concrete driver запис усе ще закритий.

Цей файл — поточний план і межі робіт, а не свідчення виконаного deployment.
Технічні checkpoints: [current-baseline.md](current-baseline.md),
[shared-writer-boundary.md](shared-writer-boundary.md).
