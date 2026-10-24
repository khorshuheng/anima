# Deployment examples

These files are examples only. In production, keep the rules JSON, the scripts
and their secrets **outside the Anima repository**. The deploy service reads a
fixed script root and a fixed rules file; a push payload can never choose or
inject code.

Suggested layout on the deployment host:

```text
/srv/anima-deploy/
  rules.json
  scripts/
    update-compose.sh
    trigger-github-actions.py
```

`rules.example.json` shows repository/ref glob rules. Each matched rule runs its
script with:

- `ANIMA_DEPLOY_REPO`
- `ANIMA_DEPLOY_REF`
- `ANIMA_DEPLOY_BEFORE`, `ANIMA_DEPLOY_AFTER`
- `ANIMA_DEPLOY_RUN_ID`, `ANIMA_DEPLOY_RULE`
- `ANIMA_DEPLOY_ENVIRONMENT`
- `ANIMA_DEPLOY_SCRIPT_ROOT`

`update-compose.sh` is the local-dev example. `trigger-github-actions.py` is the
UAT/production pattern: trigger a GitHub Actions workflow with `GITHUB_TOKEN`
instead of rebuilding on the deployment host.
