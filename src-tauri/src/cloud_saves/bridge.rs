use crate::database::repository::games_repository::GamesRepository;

use sea_orm::DatabaseConnection;
use serde_json::{Value, json};
use std::io::Write;
use std::process::{Command, Stdio};
use tauri::{AppHandle, Emitter, Manager, Runtime, State};
use tokio::sync::Mutex;

// 啟動前檢查至建立遊戲程序之間不可插入還原操作。
pub(crate) static GATE: Mutex<()> = Mutex::const_new(());

async fn call<R: Runtime>(app: &AppHandle<R>, request: Value) -> Result<Value, String> {
    if !cfg!(target_os = "windows") {
        return Err("Google Drive 存檔同步目前僅支援 Windows。".into());
    }
    let base = reina_path::get_base_data_dir()?.join("savecloud");
    let executable = app
        .path()
        .resource_dir()
        .map_err(|e| e.to_string())?
        .join("tools/savecloud/ReinaSaveCloud.exe");
    let payload = serde_json::to_vec(&request).map_err(|e| e.to_string())?;
    let response = tauri::async_runtime::spawn_blocking(move || -> Result<Value, String> {
        let mut command = Command::new(executable);
        #[cfg(target_os = "windows")]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        let mut child = command
            .arg(base)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|e| format!("無法啟動存檔同步核心，請確認完整解壓程式：{e}"))?;
        child
            .stdin
            .take()
            .ok_or("無法開啟同步通道")?
            .write_all(&payload)
            .map_err(|e| e.to_string())?;
        let output = child.wait_with_output().map_err(|e| e.to_string())?;
        if !output.status.success() {
            return Err("存檔同步核心意外結束；請重新開啟存檔頁檢查復原狀態。".into());
        }
        serde_json::from_slice(&output.stdout).map_err(|e| format!("同步回應格式錯誤：{e}"))
    })
    .await
    .map_err(|e| e.to_string())??;
    let _ = app.emit("cloud-saves-changed", ());
    Ok(response)
}

#[tauri::command]
pub async fn cloud_saves<R: Runtime>(
    app: AppHandle<R>,
    db: State<'_, DatabaseConnection>,
    mut request: Value,
) -> Result<Value, String> {
    let action = request["action"]
        .as_str()
        .ok_or("缺少操作名稱")?
        .to_string();
    if ![
        "status",
        "identify",
        "preview",
        "configure",
        "import-client",
        "login",
        "cloud-games",
        "sync",
        "backup",
        "history",
        "resolve",
        "confirm-ended",
        "pause",
        "relink",
    ]
    .contains(&action.as_str())
    {
        return Err("不支援的操作".into());
    }
    let _guard = GATE.lock().await;
    if ["identify", "preview", "configure", "relink"].contains(&action.as_str()) {
        let id = request["gameId"].as_i64().ok_or("缺少遊戲 ID")?;
        let game = GamesRepository::find_by_id(
            db.inner(),
            i32::try_from(id).map_err(|_| "遊戲 ID 不正確")?,
        )
        .await
        .map_err(|e| e.to_string())?
        .ok_or("找不到遊戲")?;
        if game.launch_type != "local" {
            return Err("請先連結本機遊戲主程式。".into());
        }
        let folder =
            reina_path::resolve_user_path(game.localpath.as_deref().ok_or("請先選擇遊戲目錄")?)
                .map_err(|e| e.to_string())?;
        request["exePath"] =
            json!(folder.join(game.executable.as_deref().ok_or("請先選擇主程式")?));
        // 名稱由介面提供；跨電腦配對一律使用明確選擇的雲端 UUID。
    }
    if let Some(slots) = request["slots"].as_array_mut() {
        for slot in slots {
            if let Some(raw) = slot["path"].as_str() {
                slot["path"] =
                    json!(reina_path::resolve_user_path(raw).map_err(|e| e.to_string())?);
            }
        }
    }
    call(&app, request).await
}

pub(crate) async fn before_launch<R: Runtime>(
    app: &AppHandle<R>,
    game_id: u32,
    exe: &std::path::Path,
    offline: bool,
) -> Result<(), String> {
    let response = call(
        app,
        json!({"action":"before-launch", "gameId":game_id, "exePath":exe, "offline":offline}),
    )
    .await?;
    if response["ok"] == true {
        Ok(())
    } else {
        Err(format!(
            "CLOUD_SAVE:{}:{}",
            response["code"].as_str().unwrap_or("error"),
            response["message"].as_str().unwrap_or("同步失敗")
        ))
    }
}

pub(crate) async fn launch_failed<R: Runtime>(app: &AppHandle<R>, game_id: u32) {
    let _ = call(app, json!({"action":"launch-failed", "gameId":game_id})).await;
}

pub(crate) fn track<R: Runtime>(app: AppHandle<R>, game_id: u32, process_id: u32) {
    tauri::async_runtime::spawn(async move {
        if let Err(error) = call(
            &app,
            json!({"action":"track", "gameId":game_id, "processId":process_id}),
        )
        .await
        {
            log::warn!("存檔追蹤失敗，保留待確認狀態：{error}");
        }
    });
}

pub fn start_retry_loop<R: Runtime>(app: AppHandle<R>) {
    if !cfg!(target_os = "windows") {
        return;
    }
    tauri::async_runtime::spawn(async move {
        loop {
            tokio::time::sleep(std::time::Duration::from_secs(60)).await;
            if let Ok(_guard) = GATE.try_lock() {
                if let Err(error) = call(&app, json!({"action":"retry"})).await {
                    log::warn!("雲端佇列重試失敗：{error}");
                }
            }
        }
    });
}

pub(crate) async fn guard_legacy_restore<R: Runtime>(
    app: &AppHandle<R>,
    game_id: i32,
) -> Result<(), String> {
    let response = call(app, json!({"action":"status", "gameId":game_id})).await?;
    if response["ok"] != true || !response["data"]["game"].is_null() {
        return Err("已設定雲端同步的遊戲，請使用 Google Drive 歷史版本還原。".into());
    }
    Ok(())
}
