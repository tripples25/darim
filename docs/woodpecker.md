# Woodpecker на VPS

Приложение: https://wishes.hype-ny.ru. CI: https://ci.hype-ny.ru.

Woodpecker управляется в Coolify: CI → production → Woodpecker.
Compose-определение хранится в `ops/woodpecker/compose.yaml`; рабочие
переменные, включая OAuth и секрет агента, хранятся в Coolify. Сервис
использует существующие внешние тома `darim-ci_server_data`,
`darim-ci_agent_data`, `darim-ci_worker_data` и `darim-ci_worker_socket`.
Старые контейнеры в `/opt/woodpecker` оставлены для отката. Их нельзя
запускать одновременно с сервисом Coolify, поскольку тома общие.

Woodpecker 3.20.1 использует SQLite в постоянном Docker volume. Регистрация
закрыта, администратор — tripples25. Только публичные репозитории этого
владельца синхронизируются через GitHub OAuth.

Агент использует отдельный Docker-in-Docker демон через Unix socket, без
доступа к Docker socket хоста. Worker ограничен 3 ГБ RAM, параллелизм — один
workflow. Это дополнительное разделение, а не изоляция уровня отдельной VM:
worker работает в privileged-контейнере. Непроверенные pull requests не должны
получать privileged-доступ, volumes или секреты деплоя. Для автоматических
тестов произвольных внешних PR нужен отдельный worker-хост.

### Вместимость CI и обслуживание кеша

Ежедневная задача `darim-docker-cleanup.timer` проверяет CI cache. Скрипт
`ops/woodpecker/cache_maintenance.py` читает статусы Woodpecker из SQLite в
режиме read-only и проверяет контейнеры вложенного Docker daemon. Любой активный,
неизвестный или неудачно прочитанный статус останавливает обслуживание.
Проверка и начало очистки защищены общим lock-файлом в `darim-ci_worker_socket`:
шаг сборки берёт shared lock, а обслуживание — exclusive lock. Если pipeline уже
активен, SQLite gate отменяет обслуживание; если он стартует после gate, его шаг
ожидает освобождения lock.

Скрипт не трогает образы, тома, Docker daemon production или журналы. При кеше не
больше 6 ГБ и свободном root-диске не меньше 20% он ничего не меняет. Иначе он
ограничивает кеш сборки бюджетом 6 ГБ, резервом 4 ГБ и целевыми 12 ГБ свободного
места. Нынешний замер — 11.17 ГБ кеша и 27 ГБ свободного диска; очистка не
запускалась, но следующий timer-run сократит кеш до бюджета. Это ограничение
смягчает рост данных, но не заменяет отдельный runner: worker и production всё
ещё используют один диск.

Для установки скрипт копируется в `/usr/local/lib/darim/cache_maintenance.py`,
а unit-файлы `darim-docker-cleanup.service` и `.timer` — в
`/etc/systemd/system/`; затем timer перечитывается и включается штатным systemd.
Сначала следует сверить текущую конфигурацию и установить Python 3, Docker CLI,
volume `darim-ci_worker_data` и путь к SQLite `darim-ci_server_data`. Lock-файл
`cache-maintenance.lock` надо заранее создать в volume `darim-ci_worker_socket`
и примонтировать к шагу `tests-and-build`, как в `.woodpecker.yaml`. На VPS его
нужно создать обычным файлом по пути `<worker_socket volume mountpoint>/cache-maintenance.lock`
до включения этой CI-конфигурации. Шаг проверяет файл через `test -f`. Скрипт
намеренно завершится без очистки, если lock-файл или база недоступны.

Порты 80/443 и HTTPS обслуживает встроенный Caddy в Coolify. Панель доступна
на coolify.hype-ny.ru. Маршруты CI и Darim хранятся в Dynamic Configurations
прокси Coolify (routes.caddy). Сеть darim-edge внешняя для Woodpecker:
она связывает встроенный Caddy, CI и production Darim. Встроенный прокси
использует на ней адрес 172.28.45.2, которому доверяет Darim.
При развёртывании CI на новом сервере эту сеть создают заранее:
`docker network create --subnet 172.28.45.0/24 darim-edge`.

## Пайплайн

Репозиторий подключён: https://ci.hype-ny.ru/repos/1. Конфигурация —
`.woodpecker.yaml`. На push в main или ручной запуск выполняются:

1. Сборка CI-окружения с .NET SDK 10.0.403, Node 24 и Chromium.
2. Locked restore, .NET unit/integration/component-тесты и Python ops-тесты.
3. Release publish и Playwright на опубликованной версии с временной БД.
4. Сборка runtime Docker-образа `darim:<commit SHA>` в отдельном CI Docker.

Образ окружения кешируется. Вебхуки PR отключены; Trusted Volumes включён
для сокета CI Docker с явного разрешения владельца. Network/Security trust
выключены. Интерфейс Woodpecker и страницы репозитория CI закрыты авторизацией.
Для запуска вручную используйте New pipeline, ветка main.

## Автоматический деплой

На push в main Woodpecker выполняет тесты, собирает runtime-образ и проверяет
именно этот образ с отдельной PostgreSQL: применяет миграции и запрашивает
`/health`. Ручной запуск выполняет только проверки.

После успешной проверки CI публикует образ в закрытый реестр
`127.0.0.1:5000/darim:<commit SHA>` и обновляет указатель `production`.
Реестр работает в сетевом пространстве CI Docker. На VPS порт опубликован
только на loopback, через публичный Caddy реестр не проксируется.

Таймер `darim-registry-deploy.timer` замечает новый digest, проверяет
совпадение с тегом коммита и запускает релиз через Coolify. Для одного digest
разрешены две автоматические повторные попытки через 15 и 30 секунд. Они
допускаются только до принятия запроса на запуск Coolify: при проблеме pull,
занятом lock, нездоровой БД или ошибке проверки pre-deploy backup. Валидный
backup при этом ещё не сопровождается запросом миграции. После передачи
запроса Coolify любая ошибка становится терминальной: reconcile не запускает
миграции повторно автоматически. Перед релизом создаётся и проверяется backup
PostgreSQL в `/opt/darim/backups`; Coolify получает образ по digest. Скрипт
проверяет образ запущенного контейнера, результат миграций и публичный HTTPS
`/health`.

Шаг `production-deployment` ждёт результат конкретного прогона и печатает
ссылку на https://wishes.hype-ny.ru/deployment.json. Ответ содержит последний
подтверждённый релиз и ссылку на его прогон Woodpecker, а также статус
последней попытки: номер повторов, `nextRetryAt`, фазу, код ошибки, класс,
`terminal` и `alert`. `attempt.alert == true` — машинно-читаемые метаданные
для системы оповещения; само поле уведомление не отправляет.
При терминальной ошибке прогон завершается неуспешно, предыдущий `current`
остаётся активным, а backup сохраняется. Автоматического отката схемы базы нет.

После проверки состояния Coolify и БД оператор может вручную повторить текущий
терминально неудачный digest командой
`sudo /opt/darim/registry-deploy/reconcile-registry.sh --retry`. Команда
сбрасывает лимит повторов для одной явной попытки; она может создать новый
backup и повторно попросить Coolify применить образ. Поэтому сначала нужно
проверить, запускались ли миграции и восстановима ли база. Без `--retry`
терминальная неудача этого digest больше не запускается.

Файлы systemd находятся в `ops/coolify`. На VPS скрипты
`reconcile-registry.sh` и `promote-coolify.sh` установлены в
`/opt/darim/registry-deploy`. Переменные production и подключение томов
`darim_postgres` и `darim_keys` управляются в Coolify.
