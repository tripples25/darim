# Woodpecker на VPS

Приложение: https://wishes.hype-ny.ru. CI: https://ci.hype-ny.ru.

Конфигурация CI находится в `ops/woodpecker/`. Она установлена на сервере
в `/opt/woodpecker`; `.env` с OAuth-данными и случайным agent secret хранится
только там с правами 600. Не коммитить этот файл и не выводить `compose config`
без `--quiet`: раскрываются секреты.

Woodpecker 3.20.1 использует SQLite в постоянном Docker volume. Регистрация
закрыта, администратор — tripples25. Только публичные репозитории этого
владельца синхронизируются через GitHub OAuth.

Агент использует отдельный Docker-in-Docker демон через Unix socket, без
доступа к Docker socket хоста. Worker ограничен 3 ГБ RAM, параллелизм — один
workflow. Это дополнительное разделение, а не изоляция уровня отдельной VM:
worker работает в privileged-контейнере. Непроверенные pull requests не должны
получать privileged-доступ, volumes или секреты деплоя. Для автоматических
тестов произвольных внешних PR нужен отдельный worker-хост.

Caddy из CI-стека занимает порты 80/443 и обслуживает оба домена. Для
production-приложения используется `compose.yaml` вместе с
`compose.woodpecker.yaml`, и запускаются только db, migrate и app; второй
Caddy из базового Compose запускать нельзя. Внешняя сеть darim-edge создаётся
CI-стеком. Адрес CI Caddy 172.28.45.2 указан в trusted proxy приложения.

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

## Деплой

После успешного прогона выберите Deploy у pipeline, цель `production`.
Deployment снова запускает проверки, затем переносит собранный образ по SSH
на VPS. Другие события не получают SSH-секреты и не запускают деплой.
Секреты Woodpecker `production_ssh_password` и `production_known_hosts`
ограничены событием deployment. SSH host key проверяется строго.

Production `.env` расположен в `/opt/darim/.env` с правами 600. В нём заданы
домен и случайные пароль БД/ключ подписи. До подключения Telegram вход
пользователей недоступен; демонстрационный вход в production выключен.

Релизы сохраняются в `/opt/darim/releases/<SHA>-<pipeline>`. При обновлении
существующего приложения перед миграциями выполняется pg_dump в
`/opt/darim/backups`. Это локальная копия: настройте отдельно off-server S3
бэкапы из README. После явных миграций запускается app, проверяется HTTPS
`/health`, затем обновляется `current-release`. Автоматического отката схемы
нет; при ошибке job завершается неуспешно. Старые релизы/образы сохраняются.

Не запускайте одновременно ручной deploy.sh и deployment из CI. При
параллелизме агента один выполняется максимум один workflow. Следите за
местом на диске и удаляйте ненужные старые образы/релизы осознанно.
