# Ограничение Docker-логов

Compose для Darim и Woodpecker использует общий профиль на сервис:

```yaml
x-rotated-logging: &rotated-logging
  driver: json-file
  options:
    max-size: "10m"
    max-file: "3"
```

Это ограничивает локальные JSON-логи каждого контейнера примерно 30 MiB.
Настройка применяется к приложению, миграциям, PostgreSQL, Caddy, резервному
копированию, сервисам Woodpecker и локальному registry. Для контейнеров CI
worker дополнительно задаёт те же параметры по умолчанию вложенному Docker
daemon, который запускает pipeline containers.

Сервер приложения уже пишет метод, маршрут, HTTP-статус, длительность и
`HttpContext.TraceIdentifier` в каждой строке запроса, поэтому request ID
позволяет найти связанные записи без логирования содержимого запросов.

## Применение в Coolify

Coolify хранит Compose-описание production service отдельно от этого репозитория.
Поэтому изменение `compose.yaml` само по себе не меняет уже запущенные контейнеры.
В Compose редакторе Darim в Coolify добавьте профиль выше и `logging: *rotated-logging`
в определения `app`, `db` и `migrate`. Повторите профиль для
Woodpecker service; для worker-docker добавьте аргументы `--log-driver=json-file`,
`--log-opt=max-size=10m` и `--log-opt=max-file=3` в команду `dockerd`.

Применение требует пересоздать соответствующие контейнеры. Согласуйте короткое
окно обслуживания для Darim и убедитесь, что в Woodpecker нет текущего job.
Не перезапускайте Docker daemon целиком. После пересоздания проверьте настройки
без вывода логов:

```sh
docker inspect --format '{{.HostConfig.LogConfig.Type}} {{json .HostConfig.LogConfig.Config}}' <container>
```

Ожидаемое значение: `json-file` с `max-size=10m` и `max-file=3`. Новая политика
ограничивает будущий рост; старый logfile очищается по мере ротации, а старые
контейнеры используют прежнюю конфигурацию, пока их не пересоздали.

`tests/ops/test_logging_config.py` проверяет итоговую конфигурацию Compose,
включая параметры вложенного CI daemon, без запуска сервисов.
