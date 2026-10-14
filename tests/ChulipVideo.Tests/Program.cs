using ChulipVideo.Core;
if (new CropRect(-3, 20, 800, 600).Clamp(640,480,false) != new CropRect(0,0,640,480)) throw new Exception("クロップ境界が不正です。");
Console.WriteLine("初期モデル検証: 成功");
