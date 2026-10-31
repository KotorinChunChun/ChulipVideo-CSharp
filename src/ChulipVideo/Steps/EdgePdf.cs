using System.ComponentModel;
using System.Diagnostics;

namespace ChulipVideo.Steps;

/// <summary>Windows標準のMicrosoft Edgeのヘッドレス印刷で、HTMLをPDFにする。</summary>
public static class EdgePdf
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static string? FindEdge()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        return roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 利用者が開いているEdgeへ処理が渡らないよう、専用の一時プロファイルで起動する。
    /// </summary>
    public static async Task PrintAsync(string edge, string html, string pdf, string profile, CancellationToken ct)
    {
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[]
        {
            "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            "--user-data-dir=" + profile, "--no-pdf-header-footer", "--print-to-pdf=" + pdf, new Uri(Path.GetFullPath(html)).AbsoluteUri
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Microsoft Edgeを起動できませんでした。");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"PDFの作成が{Timeout.TotalSeconds:0}秒以内に終わりませんでした。");
        }
        try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0) throw new IOException($"PDFが作成されませんでした（終了コード {process.ExitCode}）。");
    }
}
