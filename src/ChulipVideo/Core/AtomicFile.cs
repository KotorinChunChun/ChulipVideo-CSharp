namespace ChulipVideo.Core;

/// <summary>同じフォルダーの一時ファイルへ書いてから置き換え、書込み途中の壊れたファイルを残さない。</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, contents); File.Move(temp, full, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
