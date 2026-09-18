# ReinaManager SaveCloud

對應原始碼：[Awayuki99/ReinaManager 的 SaveCloud 分支](https://github.com/Awayuki99/ReinaManager/tree/codex/google-drive-saves)。

此分支將 SaveCloud 同步核心整合到 ReinaManager 0.29.2 的遊戲存檔頁與原生啟動流程。目前交付 Windows x64 便攜版。

## 使用

1. 將整個壓縮檔解壓到新的資料夾，執行 `ReinaManager.exe`。`tools` 和 `resources/data` 必須保留。測試版使用獨立應用程式識別碼，並停用上游自動更新。
2. 加入本機遊戲，設定遊戲目錄與 `.exe`。開啟遊戲詳情的「存檔」頁，找到「Google Drive 存檔同步」。
3. 按「自動尋找存檔」，或加入檔案／資料夾。辨識不到可以手動設定多個位置；檔案規則可用 `*.rpgsave` 等模式。
4. 按「預覽待備份檔案」，檢查內容後確認啟用。尚未遊玩時顯示尚無存檔；不要選擇整個遊戲安裝資料夾當作存檔。
5. 匯入 Google OAuth 桌面應用程式 JSON，再登入。此後任何遊戲啟動按鈕都會先同步；遊戲程序及子程序結束後會自動備份上傳。
6. 另一台電腦使用同一 OAuth 應用程式與同一 Google 帳號。在該遊戲的存檔頁按「讀取另一台電腦的遊戲」，明確選擇雲端遊戲，依序填入各存檔位置的本機路徑並確認。資料夾名稱或 ReinaManager 的數字 ID 不會用來自動配對。

原便攜版資料不會自動匯入。需要保留遊戲庫時，可先在原版備份資料庫，再從測試版的資料維護介面匯入備份；先保留原版資料夾，切勿讓兩個版本同時操作同一個資料庫。

## Google 初次設定

- 在 Google Cloud 建立專案並啟用 Google Drive API。
- 在 Google Auth Platform 設定應用程式資訊，建立「桌面應用程式」OAuth 用戶端並下載 JSON。
- 若應用程式處於測試模式，將自己登入的 Google 帳號加入 Audience 的測試使用者。`403 access_denied` 請優先檢查此處及組織管理員限制。
- 每台電腦匯入同一個用戶端設定，各自登入。使用最小 `drive.file` 權限、PKCE 與本機回呼；權杖以 Windows 使用者 DPAPI 加密。
- 測試模式的 refresh token 可能七天到期；存檔頁可重新登入。不要將 OAuth JSON、權杖或私人資料庫提交到 Git。
- 設定說明：[Google 桌面 OAuth 文件](https://developers.google.com/identity/protocols/oauth2/native-app)。

## 衝突、離線與復原

- 啟動前以檔案雜湊、同步基準和父版本判斷差異，不以電腦時鐘决定覆蓋方向。
- 雙方都更新時會阻止啟動。到存檔頁按「立即同步」，選擇完整的本機或雲端進度；被取代的本機進度會先保存。歷史版本保留，不自動清除。
- 網路或授權失敗時，啟動流程提供離線遊玩選擇。結束後建立本機 ZIP 與待上傳工作；程式每分鐘嘗試處理閒置遊戲的佇列，重新連線後仍會檢查衝突。
- 無法確認程序結束、程式在追蹤期間中斷，或存檔持續被寫入時，不會強制還原。關閉遊戲和啟動器後，使用「確認遊戲已結束」或「立即備份並上傳」。
- 還原前驗證 ZIP、雜湊與路徑，保存本機復原副本及持久化還原紀錄。下次操作會先處理中斷的還原。
- 關閉視窗會縮至系統匣。系統匣退出會提示未完成工作。同步中的遊戲不使用舊版 7z 還原按鈕；請從 Google Drive 歷史版本還原。
- 同步狀態與快照位於便攜版 `resources/savecloud`。不要在遊戲進行中刪除或複製此資料夾；不同電腦應分別建立本機狀態。

## 開發與驗證

需要 Node.js 24、pnpm、Rust 1.98.1 或更新版本、MSVC C++ Build Tools／Windows SDK、.NET SDK 10.0.401。便攜使用者不需安裝 .NET。

```powershell
pnpm install --frozen-lockfile
dotnet build savecloud/tests/GameStub
dotnet run --project savecloud/tests/SaveCloud.Tests -- savecloud
./scripts/build-savecloud.ps1
python scripts/test-savecloud-bridge.py
pnpm check
pnpm i18n:status
cargo fmt --check --manifest-path src-tauri/Cargo.toml
cargo clippy --manifest-path src-tauri/Cargo.toml --all-targets
cargo test --manifest-path src-tauri/Cargo.toml --lib
./scripts/build-savecloud.ps1 -Package
```

產物位於 `artifacts/ReinaManager-SaveCloud-win-x64.zip`。GitHub Actions 的 `SaveCloud Windows` 也可產生相同便攜包。此分支的雲端功能僅支援 Windows x64。

同步與故障測試使用隔離檔案、測試程序及模擬 Drive；真實 Google 登入、兩台實體電腦與個別遊戲讀檔仍需以自己的帳號完成驗證。

## 模組邊界與授權

- React：`CloudSaves` 頁面、`useCloudSaves` 查詢、`cloudSaveService` IPC；不直接在元件呼叫 Tauri invoke。
- Rust：`cloud_saves/bridge.rs` 只啟動隨程式附帶的背景程式，以 stdin/stdout JSON 傳遞資料，不經 shell。原生啟動與還原共用操作鎖。
- C#：`savecloud/src/SaveCloud.Bridge` 維護 ReinaManager ID → 雲端 UUID 對應、背景程序追蹤與操作鎖；`SaveCloud.Core` 管理 SQLite、快照、衝突、Google Drive 與還原紀錄。遊戲執行中以持久化標記阻擋還原，與 ReinaManager 的遊玩時間統計分開。
- Ludusavi 資料庫固定版本與 SHA-256 記錄於 `savecloud/data/manifest-version.json`；已移除與存檔無關的啟動參數；原始與精簡版雜湊皆有記錄。其授權隨附於 `LUDUSAVI-LICENSE`。
- ReinaManager 原始授權與著作權保留；本分支新增程式亦以 AGPL-3.0 提供。分發衍生版本時一併提供相應原始碼。
