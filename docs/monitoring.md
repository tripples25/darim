# Мониторинг Darim

Darim отдаёт метрики Prometheus на `/metrics` по внутренней сети Docker. По умолчанию эндпоинт возвращает `404`. Для включения задайте `Monitoring__Token` из 64 шестнадцатеричных символов. Prometheus передаёт его в заголовке `Authorization: Bearer …`. Публичный Caddy возвращает `404` для `/metrics` и его вложенных путей.

## Подготовка на сервере

Выполните команды из каталога репозитория. Создайте отдельные значения для сбора метрик и входа в Grafana.

```sh
secrets="$HOME/.config/darim-monitoring"
install -d -m 0700 "$secrets"
(umask 022; set -C; openssl rand -hex 32 > "$secrets/scrape-token")
(umask 022; set -C; openssl rand -hex 32 > "$secrets/grafana-password")
```

Режим `0700` защищает файлы на сервере. Сами файлы имеют режим `0644`, чтобы процессы Prometheus и Grafana могли читать каждый свой файл внутри контейнера. Команды сохраняют существующие значения при повторном запуске.

### Обычный Docker Compose

Сначала примените публичный маршрут Caddy. Затем включите `/metrics` в приложении через дополнительный Compose-файл.

```sh
docker compose up -d --force-recreate caddy
DARIM_MONITORING_TOKEN="$(cat "$secrets/scrape-token")" \
  docker compose -f compose.yaml -f ops/monitoring/app.compose.yaml up -d app
```

Стек мониторинга использует сеть `darim_private` и адрес `app:8080`. Если имя Compose-проекта было переопределено, передайте фактическое имя сети в `DARIM_MONITORING_NETWORK`. При обновлениях приложения сохраняйте оба `-f`, пока сбор метрик включён.

### Production в Coolify

Сначала примените блок `@metrics` из [публичного маршрута](../ops/coolify/routes.caddy) в Dynamic Configurations Caddy для Darim. На сервисе `app` в Compose Coolify добавьте переменную окружения `Monitoring__Token: ${DARIM_MONITORING_TOKEN:-}`. Задайте `DARIM_MONITORING_TOKEN` значением из `scrape-token` в настройках окружения сервиса. Если в `AllowedHosts` перечислены разрешённые хосты, добавьте к существующему списку `darim-coolify-app` через `;`. Примените настройки сервиса с образом Darim, поддерживающим `/metrics`.

Prometheus подключается к `darim-edge`, где приложение уже доступно под именем `darim-coolify-app`. Файл целей содержит внутренний адрес и не содержит секретов.

```sh
printf '%s\n' '- targets: [darim-coolify-app:8080]' > "$secrets/targets.yml"
export DARIM_MONITORING_NETWORK=darim-edge
export DARIM_PROMETHEUS_TARGETS_FILE="$secrets/targets.yml"
```

### Запуск Prometheus и Grafana

```sh
export DARIM_SCRAPE_TOKEN_FILE="$secrets/scrape-token"
export DARIM_GRAFANA_PASSWORD_FILE="$secrets/grafana-password"
docker compose -f ops/monitoring/compose.yaml up -d
```

Grafana слушает `127.0.0.1:3000` на сервере. Для удалённого входа откройте туннель `ssh -N -L 3000:127.0.0.1:3000 user@server`. Перейдите на `http://localhost:3000/d/darim-overview`, войдите как `admin` с паролем из `grafana-password`. Файл задаёт пароль при первом запуске с пустым томом Grafana. Если порт занят, задайте `DARIM_GRAFANA_PORT` и используйте тот же порт в туннеле.

Prometheus собирает данные каждые 15 секунд. Данные и настройки сохраняются в отдельных томах. Prometheus удаляет старые блоки по первому из двух пределов, 15 дней или 1 ГБ.

## Проверка и чтение дашборда

```sh
docker compose -f ops/monitoring/compose.yaml exec prometheus \
  promtool check config /etc/prometheus/prometheus.yml
docker compose -f ops/monitoring/compose.yaml exec prometheus \
  promtool query instant http://localhost:9090 'up{job="darim"}'
```

Значение `1` показывает, что Prometheus получил метрики. Проверьте отдельно, что публичный `/metrics` возвращает `404`. После нескольких запросов к Darim дашборд покажет интенсивность HTTP-запросов, долю `5xx`, задержки p50/p95/p99, результаты `/health`, создание подарков и работу Ohmywishes. Скорости и процентили требуют нескольких сборов, поэтому новые серии появляются с задержкой.

HTTP-метрики содержат шаблоны маршрутов, классы статусов и методы из фиксированного набора. Для Ohmywishes учитываются сохранённые импорты, ошибки источника, случаи переподключения и ошибки фонового процесса. Счётчики хранятся в памяти приложения и сбрасываются после перезапуска. Prometheus сохраняет историю.

Для замены токена обновите его в конфигурации Darim и в `scrape-token`, затем пересоздайте контейнеры приложения и Prometheus. Чтобы отключить сбор, уберите токен из приложения и примените конфигурацию. Стек мониторинга останавливается отдельно через `docker compose -f ops/monitoring/compose.yaml down`.
