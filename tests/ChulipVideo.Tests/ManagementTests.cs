using System.Reflection;

namespace ChulipVideo.Tests;

internal static class ManagementTests
{
    const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using (var window = NewWindow(directory, "settings.json"))
                {
                    var reason = window.GetType().GetProperty("ManagementStopBlockReason", hidden)!;
                    if (reason.GetValue(window) is not null) throw new Exception("待機中の停止が拒否されました。");
                    foreach (var name in new[] { "exporting", "loading", "closing" })
                    {
                        var flag = window.GetType().GetField(name, hidden)!;
                        flag.SetValue(window, true);
                        if (reason.GetValue(window) is not string { Length: > 0 }) throw new Exception(name + "中の管理停止を拒否しませんでした。");
                        flag.SetValue(window, false);
                    }
                    // 再生中のプレビュー描画は終了処理で取り消せるため、停止を拒否しない。
                    var rendering = window.GetType().GetField("rendering", hidden)!;
                    rendering.SetValue(window, true);
                    if (reason.GetValue(window) is not null) throw new Exception("描画中の管理停止を拒否しました。");
                    rendering.SetValue(window, false);
                    if (window.Visible || reason.GetValue(window) is not null) throw new Exception("検証後の状態が不正です。");
                }
                SaveDialogs();
                Closing(directory, CloseReason.WindowsShutDown, expectCancel: false);
                Closing(directory, CloseReason.UserClosing, expectCancel: true);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("管理停止・終了処理の検証に失敗しました。", failure);
        Console.WriteLine("PASS 管理連携: 待機時と描画中の停止許可・3状態の停止拒否・画面非表示・上書き確認なし・シャットダウン時の即時終了");
    }

    static ChulipVideo.UI.MainForm NewWindow(string directory, string settingsName) =>
        new(new ChulipVideo.Core.SettingsStore(Path.Combine(directory, settingsName)), new ChulipVideo.Core.AppSettings(), null);

    static void SaveDialogs()
    {
        var factory = typeof(ChulipVideo.UI.MainForm).GetMethod("SaveDialog", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var directory in new[] { Path.GetTempPath(), null })
        {
            using var dialog = (SaveFileDialog)factory.Invoke(null, ["PNG画像|*.png", "試験.png", directory])!;
            // 既存ファイルは選択後に拒否するため、「上書きしますか」は出さない。
            if (dialog.OverwritePrompt || dialog.FileName != "試験.png") throw new Exception("保存ダイアログが上書き確認を表示します。");
        }
    }

    static void Closing(string directory, CloseReason closeReason, bool expectCancel)
    {
        string settingsName = "closing-" + closeReason + ".json";
        string settingsPath = Path.Combine(directory, settingsName);
        if (File.Exists(settingsPath)) File.Delete(settingsPath);
        using var window = NewWindow(directory, settingsName);
        var e = new FormClosingEventArgs(closeReason, false);
        typeof(ChulipVideo.UI.MainForm).GetMethod("OnFormClosing", hidden)!.Invoke(window, [e]);
        if (e.Cancel != expectCancel) throw new Exception($"{closeReason}の終了取消が{e.Cancel}でした。");
        if (window.GetType().GetField("shutdownComplete", hidden)!.GetValue(window) is not true) throw new Exception(closeReason + "で終了処理が完了しません。");
        if (!File.Exists(settingsPath)) throw new Exception(closeReason + "で設定を保存しません。");
    }
}
