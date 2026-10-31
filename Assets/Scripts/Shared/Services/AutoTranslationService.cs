using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static class AutoTranslationService
{
#if UNITY_5_3_OR_NEWER
    static readonly TranslationRequestQueue requests = new TranslationRequestQueue(SendUnityRequestAsync);
    const string Transport = "UnityWebRequest";
#else
    static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    static readonly TranslationRequestQueue requests = new TranslationRequestQueue(client);
    const string Transport = "HttpClient";
#endif
    public static string Language(string locale) => locale == "zh_hant" || locale == "zhtw" || locale == "zh-tw" ? "zh-TW" :
        locale == "zh_hans" || locale == "zhcn" || locale == "zh-cn" ? "zh-CN" : locale == "jp" ? "ja" : locale;

    // Stage the entire operation before returning; callers never apply half a translation.
    public static async Task<Dictionary<string, Dictionary<string, string>>> TranslateAsync(
        IReadOnlyDictionary<string, string> fields, string source, IEnumerable<string> targets, CancellationToken token, Action<string> progress = null)
    {
        var output = new Dictionary<string, Dictionary<string, string>>();
        var languages = targets.Distinct().Where(t => Language(t) != Language(source)).ToArray();
        var nonempty = fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToArray();
        int completed = 0, total = languages.Length * nonempty.Length;
        progress?.Invoke($"開始：連線={Transport}，來源 {source}，目標 {string.Join(", ", languages)}，共 {total} 項；單筆佇列、間隔至少 2 秒。");
        foreach (string target in languages) {
            var translated = new Dictionary<string, string>();
            var repeated = new Dictionary<string, string>();
            foreach (var field in nonempty) {
                string context = $"[{completed + 1}/{total}] {source} → {target} / {field.Key}";
                progress?.Invoke(context + $"：開始，{field.Value.Length} 字元。");
                if (!repeated.TryGetValue(field.Value, out string value)) {
                    value = await TranslateTextAsync(field.Value, source, target, token, message => progress?.Invoke(context + "：" + message));
                    repeated[field.Value] = value;
                } else progress?.Invoke(context + "：使用相同欄位內容的翻譯結果，未發送請求。");
                translated[field.Key] = value;
                completed++; progress?.Invoke(context + "：完成。");
            }
            output.Add(target, translated);
        }
        token.ThrowIfCancellationRequested(); progress?.Invoke($"翻譯完成 {completed}/{total}，準備套用結果。"); return output;
    }
    public static async Task<string> TranslateTextAsync(string text, string source, string target, CancellationToken token, Action<string> progress = null)
    {
        if (string.IsNullOrWhiteSpace(text) || Language(source) == Language(target)) return text;
        string newline = Regex.Match(text, @"\\+n").Value;
        string plain = Regex.Replace(text.Replace("\r\n", "\n").Replace("\r", "\n"), @"\\+n", "\n");
        plain = Regex.Replace(plain, @"\[([^\[\]/]+)/[^\[\]]+\]", "$1");
        var tags = new List<string>();
        plain = Regex.Replace(plain, @"<[^<>]+>", m => { tags.Add(m.Value); return "ZXQKEEP" + (tags.Count - 1) + "QXZ"; });
        var output = new StringBuilder();
        for (int start = 0; start < plain.Length;) {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(1000, plain.Length - start);
            if (start + length < plain.Length) {
                int boundary = plain.LastIndexOfAny(new[] { '\n', ' ', '。', '.' }, start + length - 1, length);
                if (boundary >= start + length / 2) length = boundary - start + 1;
                if (char.IsHighSurrogate(plain[start + length - 1])) length--;
                foreach (Match tag in Regex.Matches(plain, @"ZXQKEEP\d+QXZ"))
                    if (tag.Index < start + length && tag.Index + tag.Length > start + length) length = tag.Index - start;
            }
            string chunk = plain.Substring(start, length);
            string leading = Regex.Match(chunk, @"^\s*").Value, trailing = Regex.Match(chunk, @"\s*$").Value;
            string content = chunk.Trim();
            if (content.Length == 0) { output.Append(chunk); start += length; continue; }
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=" + Uri.EscapeDataString(Language(source)) +
                "&tl=" + Uri.EscapeDataString(Language(target)) + "&q=" + Uri.EscapeDataString(content);
            progress?.Invoke($"文字區段 {start + 1}–{start + length}/{plain.Length}。");
            output.Append(leading).Append(await requests.TranslateAsync(url, token, progress)).Append(trailing);
            start += length;
        }
        token.ThrowIfCancellationRequested(); string result = output.ToString();
        for (int i = 0; i < tags.Count; i++) {
            string key = "ZXQKEEP" + i + "QXZ";
            if (!result.Contains(key)) throw new InvalidDataException("Translation did not preserve a game text tag.");
            result = result.Replace(key, tags[i]);
        }
        return newline.Length > 0 ? result.Replace("\r\n", "\n").Replace("\n", newline) : result;
    }
    public static string ParseResponse(string json)
    {
        var root = JArray.Parse(json);
        if (!(root.First is JArray sentences)) throw new InvalidDataException("Invalid translation response.");
        var output = new StringBuilder();
        foreach (var sentence in sentences.OfType<JArray>())
            if (sentence.First?.Type == JTokenType.String) output.Append((string)sentence.First);
        if (output.Length == 0) throw new InvalidDataException("Empty translation response.");
        return output.ToString();
    }
#if UNITY_5_3_OR_NEWER
    static async Task<HttpResponseMessage> SendUnityRequestAsync(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = UnityEngine.Networking.UnityWebRequest.Get(url);
        request.timeout = 25;
        request.SetRequestHeader("Accept", "application/json");
        request.SetRequestHeader("User-Agent", "InazumaElevenVRModCreator/1.0");
        var operation = request.SendWebRequest();
        try {
            while (!operation.isDone) { token.ThrowIfCancellationRequested(); await Task.Delay(50, token); }
            token.ThrowIfCancellationRequested();
        } catch (OperationCanceledException) { request.Abort(); throw; }
        if (request.responseCode == 0) throw new HttpRequestException("UnityWebRequest: " + request.error);
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)request.responseCode) {
            Content = new StringContent(request.downloadHandler?.text ?? "", Encoding.UTF8)
        };
        string retry = request.GetResponseHeader("Retry-After"), contentType = request.GetResponseHeader("Content-Type");
        if (!string.IsNullOrEmpty(retry)) response.Headers.TryAddWithoutValidation("Retry-After", retry);
        if (!string.IsNullOrEmpty(contentType)) {
            response.Content.Headers.Remove("Content-Type");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return response;
    }
#endif
}

// One queue shared by both UI panels. The gate covers cooldowns and retries too,
// so another caller cannot bypass a 429 backoff. Dependencies allow offline tests.
internal sealed class TranslationRequestQueue
{
    readonly Func<string, CancellationToken, Task<HttpResponseMessage>> send;
    readonly Func<DateTimeOffset> now;
    readonly Func<TimeSpan, CancellationToken, Task> delay;
    readonly SemaphoreSlim gate = new SemaphoreSlim(1);
    readonly Dictionary<string, string> cache = new Dictionary<string, string>();
    readonly Queue<string> cachedKeys = new Queue<string>();
    DateTimeOffset nextRequest;
    DateTimeOffset limitedUntil;

    internal TranslationRequestQueue(HttpClient client, Func<DateTimeOffset> now = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        : this((url, token) => client.GetAsync(url, token), now, delay) { }
    internal TranslationRequestQueue(Func<string, CancellationToken, Task<HttpResponseMessage>> send, Func<DateTimeOffset> now = null, Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        this.send = send; this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }
    internal async Task<string> TranslateAsync(string url, CancellationToken token, Action<string> progress = null)
    {
        progress?.Invoke("加入共用請求佇列。");
        await gate.WaitAsync(token);
        try {
            token.ThrowIfCancellationRequested();
            if (cache.TryGetValue(url, out string cached)) { progress?.Invoke("命中快取，未發送請求。"); return cached; }
            if (limitedUntil > now()) {
                int seconds = (int)Math.Ceiling((limitedUntil - now()).TotalSeconds);
                progress?.Invoke($"上次 429 的冷卻尚餘 {seconds} 秒，本次未發送請求。");
                throw new HttpRequestException($"Google 翻譯仍在共用冷卻中（尚餘 {seconds} 秒），未發送新請求。");
            }
            for (int attempt = 0; attempt < 3; attempt++) {
                TimeSpan wait = nextRequest - now();
                if (wait > TimeSpan.Zero) {
                    progress?.Invoke($"等待 {Math.Ceiling(wait.TotalSeconds)} 秒（共用間隔／冷卻），之後進行第 {attempt + 1}/3 次嘗試。");
                    await delay(wait, token);
                }
                token.ThrowIfCancellationRequested();
                try {
                    progress?.Invoke($"發送請求，第 {attempt + 1}/3 次嘗試。");
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await send(url, token);
                    progress?.Invoke($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}，耗時 {elapsed.Elapsed.TotalSeconds:F1} 秒。");
                    if ((int)response.StatusCode == 429) {
                        string body = await response.Content.ReadAsStringAsync();
                        progress?.Invoke($"429 回應診斷：Content-Type={response.Content.Headers.ContentType?.ToString() ?? "未提供"}；{DescribeRateLimit(body)}");
                        TimeSpan backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, attempt));
                        var retry = response.Headers.RetryAfter;
                        TimeSpan requested = retry?.Delta ?? (retry?.Date.HasValue == true ? retry.Date.Value - now() : TimeSpan.Zero);
                        if (requested <= TimeSpan.Zero) {
                            limitedUntil = nextRequest = now() + TimeSpan.FromSeconds(120);
                            progress?.Invoke("Google 未提供有效的 Retry-After；立即停止，不再盲目重試。共用冷卻 120 秒。");
                            throw new HttpRequestException("Google 拒絕請求（429，未提供 Retry-After）；已停止自動重試，本程式冷卻 120 秒。");
                        }
                        if (requested > backoff) backoff = requested;
                        limitedUntil = nextRequest = now() + backoff;
                        progress?.Invoke($"收到 429；Retry-After={retry?.ToString() ?? "未提供"}，共用冷卻 {Math.Ceiling(backoff.TotalSeconds)} 秒。" +
                            (attempt < 2 ? "等待後重試。" : "已達 3 次嘗試，停止本次翻譯並保留冷卻。"));
                        if (attempt < 2) continue;
                    }
                    response.EnsureSuccessStatusCode();
                    limitedUntil = default;
                    string result = AutoTranslationService.ParseResponse(await response.Content.ReadAsStringAsync());
                    token.ThrowIfCancellationRequested();
                    if (cachedKeys.Count >= 256) cache.Remove(cachedKeys.Dequeue());
                    cache[url] = result; cachedKeys.Enqueue(url);
                    return result;
                } finally {
                    DateTimeOffset minimum = now() + TimeSpan.FromSeconds(2);
                    if (nextRequest < minimum) nextRequest = minimum;
                }
            }
            throw new HttpRequestException("Translation rate limit exceeded (429).");
        } finally { gate.Release(); }
    }
    internal static string DescribeRateLimit(string body)
    {
        string text = (body ?? "").ToLowerInvariant();
        string kind = text.TrimStart().StartsWith("<") ? "HTML" : text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[") ? "JSON" : "文字";
        bool unusual = text.Contains("unusual traffic") || text.Contains("automated queries");
        bool captcha = text.Contains("captcha") || text.Contains("recaptcha");
        return $"格式={kind}，長度={body?.Length ?? 0}，異常流量提示={unusual}，驗證碼提示={captcha}。";
    }
}
