using System.Drawing.Imaging;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using ChulipVideo.Core;
using ChulipVideo.Media;
using ChulipVideo.Steps;
using EventLog = ChulipVideo.Export.EventLog;

namespace ChulipVideo.Ai;

/// <summary>AIへ送る1件（説明文を作る手順と、送る画像）。</summary>
public sealed record AiStepRequest(StepItem Step, int Number, byte[] Png, string? Previous);

/// <summary>APIの応答のうち使う部分。StopReasonは end_turn / refusal / max_tokens 等。</summary>
public sealed record AiReply(string StopReason, string Text);

/// <summary>
/// Claudeで手順の説明文を作る（R10）。既定では使わず、実行のたびに利用者が送信を確認する。
/// 送る画像は手順書と同じ合成（マスク・注釈を適用）で、長辺1568px以下に縮小する。応答は構造化出力のJSONに固定する。
/// </summary>
public sealed class ClaudeStepDescriber(Func<MessageCreateParams, CancellationToken, Task<AiReply>> send)
{
    public const string DefaultModel = "claude-opus-5-5";
    public const int MaxImageEdge = 1568;
    public const string System =
        "あなたは業務の手順書を作る担当者です。画像は操作の直前の画面で、赤い枠と番号が操作する位置を示します。" +
        "操作の種類と補足をもとに、利用者がこの手順で行う操作を、日本語の1文（60文字以内）で書いてください。" +
        "文は「〜をクリックします。」「〜を入力します。」「〜を押します。」の形にします。" +
        "画面に映る個人名・メールアドレス・電話番号などの個人情報や機密情報は、説明文に含めないでください。";

    /// <summary>実際にClaude APIへ送る。APIキーはWindows資格情報マネージャー、なければSDKの既定（環境変数等）から読む。</summary>
    public static ClaudeStepDescriber CreateDefault()
    {
        string? key = CredentialStore.Read(CredentialStore.AnthropicTarget);
        AnthropicClient client = key is null ? new() : new() { ApiKey = key };
        return new(async (request, ct) =>
        {
            BetaMessage message = await client.Beta.Messages.Create(request, ct).ConfigureAwait(false);
            string text = string.Concat(message.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            return new(message.StopReason is null ? "unknown" : message.StopReason.Raw().ToString(), text);
        });
    }

    public static MessageCreateParams BuildRequest(string model, AiStepRequest item)
    {
        var step = item.Step;
        string context = $"手順番号: {item.Number}\n操作: {(step.Label.Length > 0 ? step.Label : "（手動で追加した手順）")}（{step.Kind}）\n" +
            $"今の説明文: {step.Description}\n前の手順の説明文: {item.Previous ?? "（なし）"}";
        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4000,
            System = System,
            // 拒否された場合は、拒否の種類に応じた別のモデルでサーバー側が答え直す。
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat
                {
                    Schema = new Dictionary<string, JsonElement>
                    {
                        ["type"] = JsonSerializer.SerializeToElement("object"),
                        ["properties"] = JsonSerializer.SerializeToElement(new
                        {
                            instruction = new { type = "string", description = "手順の説明文（日本語の1文、60文字以内）" },
                            target = new { type = new[] { "string", "null" }, description = "操作する対象の名前。分からなければnull" }
                        }),
                        ["required"] = JsonSerializer.SerializeToElement(new[] { "instruction", "target" }),
                        ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
                    }
                }
            },
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(item.Png), MediaType = MediaType.ImagePng } },
                        new BetaTextBlockParam { Text = context }
                    }
                }
            ]
        };
    }

    /// <summary>応答から説明文を取り出す。拒否・途中終了・形式違いはnull（テンプレートの文を残す）。</summary>
    public static string? ParseInstruction(AiReply reply) => reply.StopReason == "end_turn" ? ParseInstruction(reply.Text) : null;

    public static string? ParseInstruction(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("instruction", out var instruction) || instruction.ValueKind != JsonValueKind.String) return null;
            string text = instruction.GetString()!.Trim().ReplaceLineEndings(" ");
            return text.Length is > 0 and <= 200 ? text : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>送る画像を作る。手順書と同じ合成（マスク・注釈）を通し、長辺をMaxImageEdge以下にする。</summary>
    public static async Task<List<AiStepRequest>> PrepareAsync(FfmpegService service, MediaInfo media, EventLog events, StepDocument document, IEnumerable<StepItem> steps,
        AppSettings settings, IReadOnlyList<MaskRegion> masks, CancellationToken ct = default)
    {
        var result = new List<AiStepRequest>();
        foreach (var step in steps)
        {
            int index = document.Steps.IndexOf(step);
            int number = document.Steps.Take(index + 1).Count(s => s.Enabled);
            using Bitmap image = await StepDetector.RenderStepAsync(service, media, events, step, Math.Max(1, number), document.Crop, settings, masks, ct).ConfigureAwait(false);
            double scale = Math.Min(1, MaxImageEdge / (double)Math.Max(image.Width, image.Height));
            using Bitmap sent = scale < 1 ? new Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))) : (Bitmap)image.Clone();
            using var stream = new MemoryStream();
            sent.Save(stream, ImageFormat.Png);
            string? previous = document.Steps.Take(index).LastOrDefault(s => s.Enabled)?.Description;
            result.Add(new(step, Math.Max(1, number), stream.ToArray(), previous));
        }
        return result;
    }

    /// <summary>説明文を作り、作れた件数を返す。失敗・拒否の手順はそのまま残す。</summary>
    public async Task<(int Updated, List<string> Failures)> DescribeAsync(string model, IReadOnlyList<AiStepRequest> items, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int updated = 0;
        var failures = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"AIで説明文を作っています… {i + 1} / {items.Count}");
            try
            {
                var reply = await send(BuildRequest(model, items[i]), ct).ConfigureAwait(false);
                if (ParseInstruction(reply) is string text)
                {
                    items[i].Step.Description = text;
                    items[i].Step.DescriptionSource = "Ai";
                    updated++;
                }
                else failures.Add($"手順{items[i].Number}: 説明文を受け取れませんでした（{reply.StopReason}）。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"手順{items[i].Number}: {ex.Message}");
                if (failures.Count >= 3 && updated == 0) break; // 認証や通信の失敗が続くときは打ち切る
            }
        }
        return (updated, failures);
    }
}
