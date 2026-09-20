"""Runtime check: hosted HTTP cannot start asset work, even with valid auth."""
import json
import os
import subprocess
import time
import urllib.error
import urllib.request

env = {**os.environ, "ASPNETCORE_URLS": "http://127.0.0.1:5189",
       "NOVASPARX_SHARED_TOKEN": "ci-test-token-not-a-secret", "NOVASPARX_LINK_URL": ""}
with open("metadata-service-test.log", "w+") as log:
    process = subprocess.Popen(["dotnet", "bin/Release/net10.0/NovaSparx.Backend.dll"], env=env, stdout=log, stderr=log)
    def request(route, method="GET", auth=True):
        headers = {"Authorization": "Bearer ci-test-token-not-a-secret"} if auth else {}
        req = urllib.request.Request("http://127.0.0.1:5189" + route, method=method, headers=headers)
        try:
            response = urllib.request.urlopen(req, timeout=3)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read(10241)
            assert len(body) <= 10240, route
            return response.status, json.loads(body)
    try:
        for _ in range(100):
            try:
                status, _ = request("/health", auth=False)
                if status == 200:
                    break
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(.1)
        else:
            raise AssertionError("Metadata service did not start")
        for route in ["resolve", "preview", "client-mesh", "inspect", "references", "texture", "warmup", "refresh"]:
            status, body = request("/v1/" + route + "?path=/Game/Test", "POST" if route in ("warmup", "refresh") else "GET")
            assert status == 410 and body["code"] == "CLIENT_PROCESSING_REQUIRED", (route, status, body)
        time.sleep(3)  # The removed warmup used to start two seconds after launch.
        _, health = request("/v1/health")
        assert health["providerReady"] is False and health["serverHeavyProcessing"] is False, health
        log.flush()
        log.seek(0)
        assert "provider warmup starting" not in log.read()
        print("Metadata-only runtime passed: eight heavy routes blocked; provider stays unloaded; responses <=10 KiB.")
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
