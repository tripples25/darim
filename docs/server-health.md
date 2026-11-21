# Мониторинг и обслуживание VPS «Дарим»

Мониторинг установлен как сервис Darim Monitoring в Coolify. Конфигурация
`ops/monitoring/coolify.compose.yaml` сохраняет существующие тома Grafana и
Prometheus. Для проверки Prometheus использовать `promtool check config` и
`promtool test rules host.test.yml` из каталога с правилами. Grafana показывает
дашборд `/d/darim-host`. Системный exporter слушает только на адресе
`darim-edge`, Alertmanager и blackbox-exporter доступны внутри сети Compose.

## Telegram

На VPS через штатное хранилище установить токен существующего бота в
`/opt/darim-monitoring/secrets/telegram-token`. В файл
`/opt/darim-monitoring/secrets/telegram-chats` записать оба числовых ID через
пробел, с переводом строки. Оба файла изначально принадлежат root с режимом
0600. Без секретов для проверки мониторинга используется
`alertmanager.staging.yml` с пустым получателем и пустой файл токена.

Запустить от root `bash /opt/darim-monitoring/source/ops/monitoring/render-alertmanager`,
затем послать SIGHUP контейнеру Alertmanager. Проверить журнал загрузки и
доставку в оба чата через временное тестовое правило Prometheus с метками
`host=darim-prod,resource=delivery-test,severity=warning`. После отправки
переключить правило на пустое выражение `vector(0) > 1`. Дождаться
уведомления о восстановлении и удалить временное правило. Полное падение
VPS требует отдельной проверки с другой машины.

## Уборка

Установить файлы уборки из `ops/monitoring` в `/opt/darim-maintenance`,
сервис и таймер в `/etc/systemd/system`, а tmpfiles-конфиг в
`/etc/tmpfiles.d`. Выполнить `systemd-tmpfiles --create` для этого
конфига. Не удалять и не заменять inode файла `operations.lock`, пока
Docker worker работает. Синхронизировать `ops/reconcile-registry.sh` с
установленным в `/opt/darim/registry-deploy` и обновить CI workflow. Внести
в Compose сервиса Woodpecker привязку `worker-daemon.json` и штатно
пересоздать worker в отсутствие активных сборок.

Перед включением таймера выполнить `darim-docker-cleanup --dry-run`.
Старый `darim-login-verification` удалить по точному имени только после
проверки, что он завершён и больше не нужен. Обычные CI-образы,
временные контейнеры и тома очищаются отдельно от production Registry.

Запуск удаления из Registry разрешается файлом
`/var/lib/darim-maintenance/registry-enabled` только после проверки вывода
dry-run с двумя подтверждёнными версиями и проверки pull по каждому
защищаемому digest. При сборке мусора Registry временно останавливается.
Скрипт оставляет запись восстановления до подтверждения запуска;
`ExecStopPost` пробует запустить Registry даже после прерывания.
Проверять журнал `journalctl -u darim-docker-cleanup` и график свободного
места. Frontend manifests в процедуру отбора не входят.

Для остановки уборки отключить таймер и убрать файл разрешения Registry.
Проверить, что Registry снова отвечает на `GET /v2/`. Для отката
мониторинга вернуть сохранённые версии исходного Compose Coolify и
`prometheus.yml`, затем выполнить штатный deploy.
