# Перевірка операторського дашборда — 2026-10-09

Площадка Новобудов 6 зберігає історичний id `site-khlibzavod-5`. Вибір площадки використовує каталог `/sites`; обладнання фільтрується за підтвердженою прив'язкою. `single-bess-1` — технічний id УЗЕ, не назва площадки.

PV береться з телеметрії прив'язаного Deye asset. Загальна потужність генерації береться з `/sites/overview`: PV + КГУ + позитивна потужність УЗЕ. Заряд УЗЕ не додається. FusionSolar поки не включено без підтвердженої відповідності фізичних установок.

Відсутня GridPower у джерелі показується як відсутня величина, а не нуль. PV Profiles — конфігурація сонячного прогнозу (орієнтація/потужність), не фактична генерація. Порожня картка прихована.

Підготовлені плани читаються зі стійкого сховища через GET `/site/{siteId}/prepared-plans`. Цей endpoint не активує графіки та не повертає payload команд. Підготовлений план на 2026-10-09 містить 24 вікна. Active Schedules порожні при вимкненій активації та після перезапуску runtime repository. Усі 12 прапорців керування залишаються false.

ASKUE: A+/A− та реактивні канали API є енергією за інтервал на стороні лічильника. Підтверджена формула: кВт·год = raw × Кт × Кн. Середня потужність = нормалізована енергія / (intervalSeconds / 3600). Звірено 936 каналів/інтервалів із вкладкою «Споживання» АСКУЕ. Наприклад, 871: 0,0335 × 30 × 100 = 100,5 кВт·год за 00:00–00:30; 870 R−: 0,0045 × 3000 = 13,5 квар·год. Значення 876 з 15-хвилинного API сумуються у 30-хвилинний інтервал порталу: (0,008 + 0,007) × 40 = 0,6 кВт·год.

Ефективні множники: 869/876=40; 870/871=3000; 872–875=1000; 899/900=1; 929=200. Для 872/873 доступні лише нулі, для 874/875 дані відсутні, тому їхні коефіцієнти підтверджені таблицею «Інформація про лічильник», а не ненульовою звіркою. Кт та Кн прочитані з АСКУЕ, їх добуток збігається з Кр. Це перевірений кеш, не автоматичне отримання через `/points`. Налаштування `SiteBalance:{siteId}:Meters:{meterId}:Kt` та `:Kn` задають окремі коефіцієнти. `/points.scale` не є трансформаторним коефіцієнтом. Raw зберігається; інші площадки без власної конфігурації не отримують ці множники.

Баланс формується за календарну добу Києва. Імпорт/експорт — суми відповідних каналів головних лічильників; субспоживачі 872–876 підтверджені як незалежні. Залишок = імпорт − субспоживачі + допоміжне споживання генераторів. Повний енергетичний баланс додатково потребує синхронізованого обліку всіх джерел генерації. Від'ємний залишок позначається попередженням, не перетворюється на виміряний експорт. Неповна доба/відсутні лічильники дають incomplete. На час аудиту 874 і 875 не мали показів; нуль не підставляється.

Окрему ASKUE Consumption raw-картку прибрано; таблиця нормалізованої енергії залишається у балансі. Фінансовий план ще not_configured; підготовлений графік УЗЕ не підміняє фінансовий план площадки.

На 13:08 Київ підготовка плану на 2026-10-10 блокується відсутньою ціною ENTSO-E для першої години (`entsoe-price-gap`, 2026-10-09T21:00Z). Планувальник повторює розрахунок кожні 30 секунд. Ціну не підставляємо; активувати старий план для нової дати не можна.

Графік планів має спільну числову шкалу кВт і часову вісь Києва. Кожна пара джерело/графік має окремий ряд, тому одночасні плани не перекриваються. Легенда показує тип обладнання, asset id та статус: підготовлений або активний. УЗЕ — зелений, КГУ — помаранчевий, СЕС — фіолетовий, інші/невідомі джерела — синій. Для УЗЕ додатна потужність означає розряд, від'ємна — заряд (конвенція доменної моделі). КГУ/СЕС додатна потужність означає генерацію. Серії без планових даних не домальовуються. Read-only prepared-plans повертає `equipment_kind`; це метадані, які не активують обладнання.

Докази: `artifacts/dashboard-export-20261008/askue-effective-multiplier-verification.json`, `browser-comments-audit.json`, логи Release-збірки, 23 UI тести, 6 тестів балансу та 5 тестів API (каталог, Кт та ізоляція площадок).

Фактична звірка 2026-10-09 скасувала припущення про застосування лише Кт: потрібен добуток Кт × Кн. Сирі записи не змінюються; баланс і recent consumption перераховуються під час читання. Докази збережено в askue-coefficient-reconciliation.json та первинних API/портальних таблицях.


## Source overview labels, 2026-10-09

Replaced account/provider status shorthand with source-specific measures.
Each assigned FusionSolar station has its verified display name and ID,
active generation power in kW, acquisition time in Europe/Kyiv and timestamp
quality explanation. Acquisition time is not presented as provider measurement
time. Station numeric power is not energy or installed capacity.
Deye is distinguished as unbound versus bound with unavailable measurements;
PV, load and battery generation contribution are named separately.
ASKUE shows collection status, last successful acquisition, selected accounting
day and measured grid import/export/subconsumer energy in kWh. Empty intervals
are unavailable, not measured zero. The overview is renamed to Ukrainian.
27 UI tests pass, including units, measured zero, missing station data and
cross-site source isolation. Live browser confirmed names, units and timestamps
for Zachyniaieva113; health is OK. Existing read-only UI mounts were updated in
place after saving .before-source-overview file backups; no container restart.
