# Woodpecker pipeline

Continue the authorized migration from GitHub Actions to Woodpecker on the
existing VPS. Work in the existing checkout; do not alter application behavior.

1. Add a reproducible CI tool image and shared verification script: locked NuGet
   restore, unit/integration/component tests, backup tests, Release publish,
   browser tests with a temporary PostgreSQL, then runtime Docker build.
2. Add main-only Woodpecker workflow for push/manual/deployment. Execute jobs
   inside the existing separate CI Docker daemon. Mount only its socket;
   do not expose the production host socket or credentials to ordinary builds.
3. Configure repository via its authorized API token: private CI visibility,
   no PR executions, volumes trust for this repository, deployment events.
4. Add a password-authenticated SSH deploy for the existing VPS, scoped to
   production deployment events. Verify the previously observed SSH host key.
   Deploy the built image and configuration, migrate explicitly and check HTTPS.
   Keep app .env on server; refuse deployment when it is missing.
5. Run the actual Woodpecker pipeline, diagnose failures, verify tests and image.
   Production app login requires Telegram credentials not yet provided.

Validation: Woodpecker linter, shell syntax, real server pipeline. Deployment
must fail before connecting if mandatory secrets are absent. No actual production
deployment until app configuration is ready. Record evidence and remaining setup.
