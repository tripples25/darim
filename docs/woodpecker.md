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
выключены. Интерфейс и логи репозитория CI остаются публичными.
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
совпадение с тегом коммита и запускает релиз через Coolify. Перед миграциями
создаётся и проверяется бэкап PostgreSQL в `/opt/darim/backups`. Coolify
получает образ по digest. Скрипт проверяет образ запущенного контейнера,
результат миграций и публичный HTTPS `/health`.

Шаг `production-deployment` ждёт результат конкретного прогона и печатает
ссылку на https://wishes.hype-ny.ru/deployment.json. Ответ содержит последний
подтверждённый релиз и ссылку на его прогон Woodpecker, а также статус
последней попытки. При ошибке прогон завершается неуспешно и бэкап сохраняется.
Автоматического отката схемы базы нет.

Файлы systemd находятся в `ops/coolify`. На VPS скрипты
`reconcile-registry.sh` и `promote-coolify.sh` установлены в
`/opt/darim/registry-deploy`. Переменные production и подключение томов
`darim_postgres` и `darim_keys` управляются в Coolify.
