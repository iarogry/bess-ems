# Планування EMS і виконання через інтеграції

EMS запускає підготовку наступної місцевої доби о **15:00 Europe/Kyiv**.
Це hosted service самого застосунку, з опитуванням кожні 30 секунд.
ENTSO-E і свіжа телеметрія потрібні перед оптимізацією; якщо ціни ще не
опубліковано або телеметрія непридатна, наступне опитування повторює підготовку.
Запис в обладнання не виконується о 15:00.

Послідовність: запуск EMS → фізична модель обладнання та ENTSO-E → оптимізація
добової потужності → компілятор конкретного API → атомарне збереження плану
і дій → виконання дій у час, визначений інтеграцією.

## Межі моделей

`EmsDayAheadPlanner` вибирає `IEquipmentDayAheadPlanningModel` за `EquipmentKind`,
а `IEquipmentScheduleCompiler` і `IEquipmentPlanExecutor` — за `IntegrationId`.
Кількість дій, формат payload і час активації не задані універсальним планом.
Модель та інтеграція вибираються незалежно одна від одної.

`Planning:ActivationEnabled=false` зберігає підготовку планів, але вимикає
виконання їх дій для всіх інтеграцій, включно з публікацією tracking schedule.

Реалізована фізична модель батареї використовує зареєстровану ємність,
потужність заряду/розряду, ККД, SOC, робочу температуру, резерви та налаштовувану
вартість проходження енергії через батарею. `ThroughputCostPerKwh` має валюту
`PriceUnit` на кВт·год: при `UAH/MWh` це грн/кВт·год. Цей параметр замінює
host default для конкретної батареї, включно з явним нулем. `ReserveSocPercent`
підвищує робочу нижню межу SOC незалежно від виробника. Фізичні параметри
залишаються в конфігурації asset.

Інші фізичні моделі (генератор, PV, кероване навантаження) підключаються через
той самий порт; їх оптимізаційні моделі та нові API-драйвери цією зміною не
реалізовано. Поточна оптимізація незалежна для кожного asset, без спільного
балансу та обмеження потужності всієї площадки. Наявна маршрутизація telemetry
і command sink у host зберігається; зокрема Deye telemetry досі вимагає одного
aggregate asset. Сам список Targets не додає маршрутизацію різних драйверів
до одного процесу.

## Стан батареї на наступну добу

Вимір SOC перевіряється за vendor timestamp і received timestamp, quality,
availability, температурою та робочим SOC. Максимальний вік — п'ять хвилин,
також діє більш строгий налаштований snapshot max age.

На північ SOC прогнозується за залишком чинного розкладу із ККД та межами
ємності. За відсутності чинного розкладу використовується явно позначене
`hold-latest-soc-assumption`. Це припущення про збереження SOC, не вимір
майбутнього стану. Воно потребує уточнення, якщо поточний legacy writer
продовжує змінювати батарею після 15:00. Оптимізатор залишає terminal SOC
вільним у робочому діапазоні; його objective не дорівнює повному фінансовому
результату площадки з `SiteEconomics`.

## Особливості інтеграцій

Для **Deye Cloud** компілятор формує Z1–Z4, кожне з шістьма TOU-рядками на
повну добу. Активації: 23:55 попереднього дня, 05:55, 11:55 і 17:55 дня
постачання за Києвом. Deadline старту — 15 хвилин. Невміщуваний у шість
рядків розклад, перевищення потужності та доби DST на 23/25 годин блокуються
адаптером. SOC-команди обмежені фізичним діапазоном asset; резерв Deye має
бути врахований у моделі EMS через робочу межу asset або `ReserveSocPercent`.
Обрізання довільної оптимізації до доступних слотів не відбувається.

Для існуючих **Modbus/MQTT/OPC UA** tracking integration публікує повний
розклад на початку доби. Наявний control cycle і відповідний command sink
виконують його надалі. Чотири вікна та шість TOU-рядків до цих інтеграцій
не застосовуються. Універсальна модель підтримує 23/24/25 часових кроків.

## Збереження і спостереження

`PlanDirectory` мусить бути постійним volume з правами лише для EMS.
Ключ файлу — SHA-256 asset ID та дата постачання. План незмінний для цієї
пари; зміна параметрів впливає на нові добові плани. Чинний план сьогодні
не замінюється завтрашнім о 15:00. Конкурентні підготовки публікують одного
переможця; незавершені `.tmp` не читаються після restart. Публікація
захищає від process crash; гарантія переживання power loss залежить від
файлової системи та persistent volume. Optimization run також записується
в налаштований audit repository; невдала публікація може лишити зайвий
audit run, але не частковий план.

Авторизований GET `/assets/{assetId}/day-ahead-plans/{deliveryDate}` показує
розклад, параметри, SOC/basis, run ID і час дій. Відповідь не містить broker
token, pilot bindings чи vendor payload. Відсутній план повертає 404.
Попередження `3101` означає проблему підготовки, `3102` — виконання дії.

## Shadow і broker

При `ShadowModeEnabled` проєкція для shadow бере **збережений план конкретної
дати**, не поточний live schedule. Автоматичне порівняння запускається лише
коли є готовий legacy-сценарій; незбіг не стає автоматичним дозволом запису.
Старі Deye shadow/activation контракти збережені для сумісності, їх правило
чотирьох вікон не використовується універсальним планувальником.

При `Planning:ActivationEnabled=true` і `DayAuthorizationsEnabled=false`
Deye pilot executor шукає operator-managed
binding `<SHA256(assetId)>-YYYY-MM-DD-Zn.json` у `ActivationBindingsDirectory`:

```json
{ "SessionId": "<armed-pilot-session-uuid>", "ClaimId": "<stable-claim-uuid>", "ExecutorId": "ems-scheduler" }
```

UUID беруться з реальної підготовки пілота; наведені placeholders не придатні
для запуску. SessionId має відповідати конкретному вікну і схваленому payload.
Немає активної сесії або binding — немає виклику broker. Це поточний механізм
контрольованого пілота, не автономна активація всіх чотирьох вікон за одним
ранковим схваленням: pilot session живе максимум 15 хвилин. Поточний outbox виконує одне вікно
для одного proposal; для наступного потрібні нове shadow-порівняння, окремі
proposal/approval і pilot session. Для щоденного автоматичного виконання використовується окремий добовий
дозвіл, описаний нижче. Планувальник не створює approvals чи writer ownership.

Перед виконанням звіряються дата, площадка, вікно і hash схваленого плану.
Чинний durable prewrite claim перевіряє writer lease, fencing і safety;
HTTP dispatcher передає в окремий HTTPS broker лише authority reference.
Доступ налаштовується через `Planning:BrokerBaseUrl` і secret
`Bess__Planning__BrokerToken`, product-agent credential із відповідним site/owner.
Token не додається до JSON-планів. Redirects і транспортні retries вимкнені.
Успіх вимагає broker state `Verified`; інша/втрачена відповідь означає
reconciliation. Replay claim не надсилає команду повторно. Broker зберігає
свої незалежні admission, mutation gate і read-back перевірки.

## Добовий дозвіл Deye

При `Planning:DayAuthorizationsEnabled=true` використовується окремий
`DeyeDayPlanExecutor`. Планування та формат фізичної моделі EMS не змінюються.
Замість чотирьох ручних pilot bindings оператор один раз явно дозволяє
автоматичне виконання **конкретного схваленого добового плану**.

1. EMS готує та зберігає план на дату, виконується реальне shadow-порівняння.
2. Наявні proposal/approval endpoints створюють four-eyes approval. Добовий
   дозвіл не створює його сам і не перетворює незбіг на схвалення.
3. Саме незалежний reviewer цього proposal викликає POST
   `/agent/activation-proposals/{proposalId}/day-authorization` перед Z1:

```json
{ "authorization_id": "<new-uuid>", "safety_revision": 2, "reason": "<operator reason>" }
```

Приклад revision 2 — placeholder: потрібна фактична поточна revision стану.
Owner береться із server configuration, не з HTTP-body. Перевіряються hash
усіх чотирьох збережених дій, дата/площадка, статус approved, незалежний
reviewer, готове equivalent shadow, поточне право ProductAgent, kill switch
і доказ зупинки legacy writer. Дозвіл не можна видати після першого trigger.

Міграція `0023_deye_day_authorizations.sql` зберігає один immutable дозвіл
на площадку/дату і окремий one-shot claim на кожне вікно. Дозвіл діє до
останнього start deadline Z4, не на іншу добу. На кожному trigger scheduler
заново отримує короткий writer lease; claim прив'язаний до фактичних owner,
safety revision, fencing token, повного hash і window hash. Зміна safety
revision, відкликання дозволу, kill switch чи lease loss блокує запис.

Broker **окремо** потребує `Broker:DayAuthorizationsEnabled=true`; default false
не приймає такі claims навіть при ввімкненні режиму EMS. Використовується
чинна ProductAgent credential з owner, що дорівнює `Planning:WriterOwnerId`.
Брокер повторно перевіряє дозвіл під час admission, initiation і безпосередньо
перед mutation, а payload читає з довіреного approved proposal. Відомості
про клієнтський успіх не замінюють broker read-back.

GET `/agent/sites/{siteId}/day-authorizations/{deliveryDate}` показує стан.
POST `/agent/day-authorizations/{authorizationId}/revoke` з body
`{ "reason": "<operator reason>" }` відкликає дозвіл для наступних записів.
Це не undo вже застосованої TOU-команди. Нова авторизація чи закінчення lease
не знімають broker Unknown latch. Claim, збережений перед process crash або
мережевим timeout, автоматично не надсилається повторно; навіть якщо падіння
сталося перед transport, потрібна явна reconciliation, можливий пропуск
вікна. Повторні запуски не обходять at-most-once правило.

Для цього режиму потрібні `ActivationEnabled`, `DayAuthorizationsEnabled`,
`WriterOwnerId`, PostgreSQL та незалежно налаштований HTTPS broker. Чинні
pilot/cutover flags залишаються prerequisites перенесення права запису.
Pilot bindings не потрібні в добовому режимі. Наявні Modbus/MQTT/OPC UA
інтеграції не використовують ці Deye permissions чи правило чотирьох вікон.

## Увімкнення

Початкова конфігурація: `config/examples/ems-planning.json`; планування, активація та добовий режим
в ній вимкнені. Цей файл треба явно додати до IConfiguration host або
перенести його розділ у налаштування сервера — він не читається автоматично.
Для підготовки потрібні `Bess:PriceSeriesSource=entso-e`, ENTSO-E secret,
domain code, `Bess:ScheduleSolver:Backend=or_tools`, справжня telemetry,
configured asset і persistent PlanDirectory. MarketBidArea має збігатися
з доменом драйвера, валюта — із відповіддю ENTSO-E. Приклад ставки 0
не є підтвердженою собівартістю.

Вимкнути старий `Bess:ShadowSchedulerEnabled`: щоденний trigger належить EMS.
Стару зовнішню автоматизацію планування 15:00 прибирати під час узгодженого
cutover; старий writer — тільки після реальних shadow і доказу sole writer.
Для Deye activation також потрібні вже наявні pilot/cutover flags, PostgreSQL,
HTTPS broker та його незалежне налаштування. У cloud-середовищі цього завдання
немає VPN, серверних secrets або vendor egress, тому ці зміни **не розгорнуті
і не ввімкнені на сервері**.

## Перевірка реалізації 8 жовтня 2026

Усі 885 локальних тестів пройшли: Application 495, API 90, architecture/host
57, optimization adapters 131, PostgreSQL persistence/broker 112.
Strict build обох host-проєктів пройшов без послаблення аналізаторів.
Перевірено весь scheduler → solver → saved plan → чотири HTTP broker actions,
concurrent claims, restart/replay, revocation на mutation boundary та Unknown,
що блокує наступне вікно. У тестах використовувалися контрольовані ціни,
telemetry, approval/evidence та записувальні fake drivers, не live Deye.
Тимчасову PostgreSQL 16.15 після тестів зупинено і видалено.

Схема містить міграцію 0023. Дозволена версія checkpoint для старого
Regelleistung dedupe tracker також піднята до 23; future-schema rejection
збережено. При deployment/rollback потрібно врахувати цей compatibility guard:
старий binary із ceiling 22 відмовиться приймати activation checkpoint зі
схеми 23. Не видаляти migration journal для обходу цієї перевірки.
