using ChulipVideo.Core;
if (new CropRect(-3, 20, 800, 600).Clamp(640,480,false) != new CropRect(0,0,640,480)) throw new Exception("クロップ境界が不正です。");
Console.WriteLine("初期モデル検証: 成功");
ChulipVideo.Tests.CanvasTests.Run();
ChulipVideo.Tests.EditingControlTests.Run();
ChulipVideo.Tests.ManagementTests.Run(Path.Combine(args.Length >= 2 ? args[1] : Path.GetTempPath(), "chulip-management-tests"));
ChulipVideo.Tests.SafetyTests.Run(Path.Combine(args.Length >= 2 ? args[1] : Path.GetTempPath(), "chulip-safety-tests"));
if(args.Length>=2)
{
    Directory.CreateDirectory(args[1]);
    ChulipVideo.Tests.SettingsTests.Run(args[1]);
    await ChulipVideo.Tests.MediaTests.RunAsync(args[0],args[1]);
    await ChulipVideo.Tests.ExportTests.RunAsync(args[0],args[1]);
}
