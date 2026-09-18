"""在隔離目錄測試實際 JSON IPC、首次確認、離線佇列與程序結束備份。"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

repo = Path(__file__).resolve().parent.parent
local_dotnet = repo.parent / ".tools/dotnet"
if local_dotnet.is_dir():
    os.environ["DOTNET_ROOT_X64"] = str(local_dotnet)
worker = repo / "src-tauri/tools/savecloud/ReinaSaveCloud.exe"
with tempfile.TemporaryDirectory(prefix="reina-存檔-") as temporary:
    root = Path(temporary)
    data = root / "資料"
    game = root / "遊戲"
    game.mkdir()
    stub = repo / "savecloud/tests/GameStub/bin/Debug/net10.0"
    shutil.copytree(stub, game, dirs_exist_ok=True)
    exe = game / "GameStub.exe"
    saves = game / "www/save"
    saves.mkdir(parents=True)
    (game / "www/js").mkdir()
    (game / "www/js/rpg_core.js").write_text("fixture", encoding="utf-8")
    save = saves / "file1.rpgsave"
    save.write_text("本機進度", encoding="utf-8")

    def call(action, **fields):
        result = subprocess.run([str(worker), str(data)], input=json.dumps({"action": action, "gameId": 1, **fields}), text=True, encoding="utf-8", capture_output=True, timeout=40, check=True)
        return json.loads(result.stdout)

    candidate = call("identify", exePath=str(exe))
    assert candidate["ok"], candidate
    slots = candidate["data"]["slots"]
    assert len(slots) == 1 and slots[0]["patterns"] == ["*.rpgsave"], candidate
    preview = call("preview", name="測試", exePath=str(exe), slots=slots)
    assert preview["ok"] and preview["data"]["total"] == 1, preview
    assert not call("configure", exePath=str(exe), slots=slots)["ok"]
    configured = call("configure", name="測試", exePath=str(exe), slots=slots, confirmed=True)
    assert configured["ok"], configured
    cloud_id = configured["data"]["game"]["id"]
    assert not call("before-launch", exePath=str(exe), offline=True)["ok"]
    denied = call("before-launch", exePath=str(exe))
    assert not denied["ok"] and denied["code"] == "auth", denied
    assert save.read_text(encoding="utf-8") == "本機進度"
    assert call("before-launch", exePath=str(exe), offline=True)["ok"]
    assert not call("sync")["ok"]
    assert not call("pause")["ok"]
    (game / "spawn-child").touch()
    process = subprocess.Popen([str(exe)], creationflags=subprocess.CREATE_NO_WINDOW)
    started = time.monotonic()
    result = call("track", processId=process.pid)
    process.wait(timeout=10)
    assert time.monotonic() - started >= 1.0, result
    assert result["ok"] and not result["data"]["active"], result
    assert result["data"]["pending"] == 1, result
    assert result["data"]["game"]["id"] == cloud_id
    assert call("backup")["code"] == "auth"
    assert call("status")["data"]["pending"] == 1
    assert call("pause")["data"]["enabled"] is False
    assert call("before-launch", exePath=str(exe))["ok"]
    assert call("pause")["data"]["enabled"] is True
    assert not call("configure", exePath=str(exe), slots=slots, confirmed=True)["ok"]
    print("PASS: IPC recognition, preview, confirmation, stable ID, offline consent, active guard, observer, queue idempotency, pause/resume")
