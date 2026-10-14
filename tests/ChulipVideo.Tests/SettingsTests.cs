using ChulipVideo.Core;

namespace ChulipVideo.Tests;
public static class SettingsTests
{
    public static void Run(string directory)
    {
        var path=Path.Combine(directory,"設定試験.json");var store=new SettingsStore(path);
        var settings=new AppSettings{Speed=-1.3,Loop=true,FfmpegPath="日本語の実行ファイル.exe"};
        settings.Presets.Add(new("試験",123,456));store.Save(settings);
        var loaded=store.Load();Check(loaded.Speed==-1.3&&loaded.Loop&&loaded.Presets[^1].Width==123,"共通設定の往復");
        File.WriteAllText(path,"{");Check(store.Load().Speed==1&&store.Warning is not null,"破損設定からの復帰");
        store.Save(settings);Check(Directory.GetFiles(directory,"設定試験.json.corrupt-*.json").Length==1,"破損設定の原本保全");
        File.WriteAllText(path,"{\"Presets\":[null],\"Overlay\":{\"RippleDuration\":1e100,\"PointerFill\":null,\"FontFamily\":null}}");
        var malformed=store.Load();Check(malformed.Presets.Count==0&&malformed.Overlay.RippleDuration==10&&malformed.Overlay.PointerFill==""&&malformed.Overlay.FontFamily is not null,"null要素と過大な設定の補正");
        var video=Path.Combine(directory,"設定対象.mp4");var original=Path.ChangeExtension(video,".settings.json");
        const string python="{\"crop_rect\":{\"x1\":10,\"y1\":20,\"x2\":110,\"y2\":100},\"start_time\":0.2,\"end_time\":1.8,\"current_time\":0.7,\"allow_oversize\":false}";
        File.WriteAllText(original,python);var info=new MediaInfo(video,160,120,2,10);
        var saved=SettingsStore.LoadVideo(info,out var warning);
        Check(warning is null&&saved.Crop==new CropRect(10,20,100,80)&&saved.Position==.7,"Python動画設定の互換読込");
        SettingsStore.SaveVideo(info,saved with{Position=1.1});
        Check(File.ReadAllText(original)==python,"Python設定の原本保持");
        Check(SettingsStore.LoadVideo(info,out _).Position==1.1,"C#設定を優先して復元");
        File.WriteAllText(SettingsStore.VideoPath(video),"{\"Crop\":{\"X\":-999,\"Y\":999,\"Width\":0,\"Height\":99999},\"Start\":999,\"End\":-1,\"Position\":999}");
        var bounded=SettingsStore.LoadVideo(info,out _);
        Check(bounded.Crop.Width==1&&bounded.Crop.Height==120&&bounded.Start<=1.9&&bounded.End<=2&&bounded.End>bounded.Start,"動画設定の境界補正");
        var shortSettings=SettingsStore.LoadVideo(info with{Duration=.03,Fps=30},out _);
        Check(shortSettings.End==.03&&shortSettings.Start==0,"1フレーム動画の範囲補正");
        Check(new CropRect(-10,-20,200,160).Clamp(160,120,true)==new CropRect(-10,-20,200,160),"枠外指定の保持");
        Check(!Directory.GetFiles(directory,"*.tmp").Any(),"設定保存の一時ファイル回収");
        Console.WriteLine("設定検証: 成功（保存復元、Python互換、原本保持、破損、境界、一時ファイル）");
    }
    private static void Check(bool condition,string label){if(!condition)throw new Exception("設定検証失敗: "+label);}
}
