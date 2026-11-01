# Guildhest Queue

國際服 Dalamud API 15 的獨立公會令排隊插件。戰鬥與移動由玩家自行操作，不需要 CBT、Yes Already、AutoDuty 或其他插件。

## 功能

- 選擇指定公會令，或 Guildhests 隨機任務。
- 自動登記、配對成功後確認進場。
- 接收到任務完成事件後，等待設定秒數再退場；也可以手動退場。
- 退場後再次登記，可設定完成次數上限或持續循環。
- 開始／停止按鈕；登出、切換角色／職業、取消排隊或提前離場時停止。
- 不修改任務搜尋器的解除限制、探索模式、語言及其他設定。

## 安裝及使用

在 Dalamud 自訂插件庫加入：

```text
https://raw.githubusercontent.com/WayneXXDD/DalamudPlugins/main/pluginmaster.json
```

這是 **Testing 測試版**。在 /xlsettings → Experimental 啟用 **Get plugin testing builds**，刷新 /xlplugins 並搜尋 **Guildhest Queue**。

1. 輸入 /ghq。
2. 選擇公會令或公會令隨機任務。
3. 設定重複次數（0 為持續）、自動確認進場、自動退場與等待時間。
4. 按「開始循環」。插件會處理第一次登記。
5. 自行操作戰鬥。完成並退場後插件會再次登記。
6. 按「停止循環」即可停止後續操作；若已排隊，可在遊戲內取消。

指令：`/ghq`、`/ghq start`、`/ghq stop`。

公會令需要已解鎖並符合遊戲的登記條件；組隊時由隊長啟動。若登記失敗，插件會停止並顯示狀態，不會持續重送。第一次使用可先設定完成 1 次。

## 建置與發布

原始碼位於 GuildhestQueue/。GitHub Actions 在 main 更新後執行核心流程測試，以 Global Dalamud API 15 編譯，驗證 ZIP 的版本與必要檔案，再發布到 plugin-repo 分支。CI 不需要把專案下載到使用者電腦。

下載檔案：[latest.zip](https://raw.githubusercontent.com/WayneXXDD/GuildhestQueue/plugin-repo/plugins/GuildhestQueue/latest.zip)。

此專案沒有第三方插件執行依賴；使用 Dalamud 與隨附 FFXIVClientStructs / Lumina 的介面。核心測試驗證排隊、進場、完成與退場的條件，不能取代實際遊戲驗證。目前以測試版發布，完整公會令循環仍需遊戲內確認。

## 授權

MIT，詳見 LICENSE。
