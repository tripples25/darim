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

Caddy из CI-стека занимает порты 80/443 и обслуживает CI и Darim. Он подключён к внешней сети Coolify для панели управления. Для доступа к панели через HTTPS нужна DNS-запись coolify.hype-ny.ru, указывающая на VPS. Production Darim подключён к darim-edge и доступен через внешний Caddy.

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
Woodpecker передаёт на VPS готовый образ `darim:<commit SHA>` по SSH,
после чего Coolify разворачивает его в Darim → production → Darim.
Если образ уже удалён из CI Docker, сначала повторите обычный pipeline.
SSH-секреты `production_ssh_password` и `production_known_hosts` доступны
только событию deployment. Проверка ключа SSH-сервера обязательна.

Перед релизом скрипт сохраняет PostgreSQL в `/opt/darim/backups` и проверяет
читаемость архива. Затем Coolify запускает миграции и приложение. Релиз
считается успешным, когда запущен именно переданный образ, миграции завершились
с кодом 0, а публичный HTTPS `/health` отвечает успешно. Последний успешный
релиз записывается в `/opt/darim/current-coolify-release`. Бэкапы локальные.
Автоматического отката схемы базы нет.

Переменные production хранятся в Coolify. Тома `darim_postgres` и
`darim_keys` подключены к production как внешние. Для этого сервера
используйте Deploy из Woodpecker, чтобы сохранить проверку образа и бэкап.
Старый Compose-деплой и ручной запуск его контейнеров нельзя использовать
одновременно с Coolify, так как они используют те же тома.
