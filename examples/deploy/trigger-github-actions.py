#!/usr/bin/env python3
"""Trigger a GitHub Actions deployment workflow for a pushed ref.

Copy this outside the Anima repo and point a deploy rule at it. The service
provides ANIMA_DEPLOY_REPO/REF/AFTER in the environment; GITHUB_TOKEN and the
workflow file are deployment-host configuration, not repository content.
"""

import json
import os
import urllib.request

repo = os.environ["ANIMA_DEPLOY_REPO"]
ref = os.environ.get("ANIMA_DEPLOY_REF", "refs/heads/main")
if ref.startswith("refs/heads/"):
    ref = ref.removeprefix("refs/heads/")

workflow = os.environ.get("GITHUB_DEPLOY_WORKFLOW", "deploy.yml")
environment = os.environ.get("ANIMA_DEPLOY_ENVIRONMENT", "dev")
token = os.environ["GITHUB_TOKEN"]

url = f"https://api.github.com/repos/{repo}/actions/workflows/{workflow}/dispatches"
body = json.dumps({"ref": ref, "inputs": {"environment": environment}}).encode()
request = urllib.request.Request(
    url,
    data=body,
    method="POST",
    headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2022-11-28",
        "Content-Type": "application/json",
    },
)
with urllib.request.urlopen(request, timeout=30) as response:
    print(f"dispatched {workflow} for {repo}@{ref}: HTTP {response.status}")
