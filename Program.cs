using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

// Render передаёт порт через PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddHostedService<KufarWatcher>();
builder.Services.AddHostedService<BotListener>();
builder.Services.AddHostedService<SelfPinger>();

var app = builder.Build();
app.MapGet("/", () => "FlatSearcher is running");
app.MapGet("/health", () => Results.Ok());
app.Run();

class KufarWatcher(IHttpClientFactory httpFactory, ILogger<KufarWatcher> log) : BackgroundService
{
    const string KufarUrl =
        "https://api.kufar.by/search-api/v2/search/rendered-paginated" +
        "?cat=1010&typ=let&cur=USD&lang=ru&size=30&sort=lst.d&rgn=4&ar=12" +
        "&gtsy=country-belarus~province-mogilyovskaja_oblast~locality-bobrujsk";

    internal static string LastRaw = "";

    internal static readonly string BotToken = Environment.GetEnvironmentVariable("TG_BOT_TOKEN") ?? "";
    internal static readonly string ChatId = Environment.GetEnvironmentVariable("TG_CHAT_ID") ?? "";
    static readonly int IntervalSec = int.TryParse(Environment.GetEnvironmentVariable("POLL_INTERVAL"), out var i) ? i : 60;
    static readonly int TestSendLast = int.TryParse(Environment.GetEnvironmentVariable("TEST_SEND_LAST"), out var t) ? t : 0;
    static readonly string StateFile = Path.Combine(AppContext.BaseDirectory, "seen.json");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (BotToken == "" || ChatId == "")
        {
            log.LogError("Не заданы TG_BOT_TOKEN и/или TG_CHAT_ID");
            return;
        }

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.Timeout = TimeSpan.FromSeconds(30);

        var seen = LoadSeen();
        var firstRun = seen is null;
        seen ??= [];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ads = await FetchAds(http, ct);
                var fresh = ads.Where(a => !seen.Contains(a!["ad_id"]!.ToString())).ToList();

                if (firstRun)
                {
                    // при первом запуске только запоминаем текущие; TEST_SEND_LAST=N шлёт N последних для проверки
                    fresh = fresh.Take(TestSendLast).ToList();
                    await Notify(http, "Слежение за квартирами в Бобруйске запущено.", ct);
                    firstRun = false;
                }

                fresh.Reverse();
                foreach (var ad in fresh)
                    await Notify(http, Format(ad!), ct);

                foreach (var a in ads) seen.Add(a!["ad_id"]!.ToString());
                File.WriteAllText(StateFile, JsonSerializer.Serialize(seen));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Ошибка опроса");
            }

            await Task.Delay(TimeSpan.FromSeconds(IntervalSec), ct);
        }
    }

    internal static async Task<List<JsonNode?>> FetchAds(HttpClient http, CancellationToken ct)
    {
        LastRaw = await http.GetStringAsync(KufarUrl, ct);
        return JsonNode.Parse(LastRaw)?["ads"]?.AsArray().ToList() ?? [];
    }

    internal static string Format(JsonNode ad)
    {
        var p = new Dictionary<string, string>();
        foreach (var x in ad["ad_parameters"]?.AsArray() ?? [])
            if (x?["p"] is { } k && x["vl"] is { } v) p[k.ToString()] = v.ToString();

        static string Money(JsonNode? n) =>
            long.TryParse(n?.ToString(), out var v) ? (v / 100).ToString() : "?";

        var lines = new List<string>
        {
            ad["subject"]?.ToString() ?? "Новая квартира",
            $"Цена: {Money(ad["price_usd"])} $ ({Money(ad["price_byn"])} BYN)",
        };
        if (p.TryGetValue("rooms", out var rooms)) lines.Add($"Комнат: {rooms}");
        if (p.TryGetValue("size", out var size)) lines.Add($"Площадь: {size} м²");
        if (p.TryGetValue("address", out var addr)) lines.Add($"Адрес: {addr}");
        lines.Add(ad["ad_link"]?.ToString() ?? "");
        return string.Join("\n", lines);
    }

    internal const string ButtonText = "Последнее объявление";

    internal static async Task Notify(HttpClient http, string text, CancellationToken ct)
    {
        var resp = await http.PostAsync(
            $"https://api.telegram.org/bot{BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = ChatId,
                ["text"] = text,
                ["reply_markup"] = $"{{\"keyboard\":[[{{\"text\":\"{ButtonText}\"}}]],\"resize_keyboard\":true}}",
            }),
            ct);
        resp.EnsureSuccessStatusCode();
    }

    static HashSet<string>? LoadSeen()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StateFile)); }
        catch (FileNotFoundException) { return null; }
    }
}

// Long polling: по нажатию кнопки присылает самое свежее объявление
class BotListener(IHttpClientFactory httpFactory, ILogger<BotListener> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (KufarWatcher.BotToken == "" || KufarWatcher.ChatId == "") return;

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.Timeout = TimeSpan.FromSeconds(60);
        long offset = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{KufarWatcher.BotToken}/getUpdates?timeout=30&offset={offset}";
                var updates = JsonNode.Parse(await http.GetStringAsync(url, ct))?["result"]?.AsArray() ?? [];

                foreach (var u in updates)
                {
                    offset = u!["update_id"]!.GetValue<long>() + 1;
                    var msg = u["message"];
                    // отвечаем только владельцу бота
                    if (msg?["chat"]?["id"]?.ToString() != KufarWatcher.ChatId) continue;
                    if (msg["text"]?.ToString() is not (KufarWatcher.ButtonText or "/start" or "/last")) continue;

                    var ads = await KufarWatcher.FetchAds(http, ct);
                    await KufarWatcher.Notify(http,
                        ads.Count > 0 ? KufarWatcher.Format(ads[0]!) : $"Объявлений нет. Ответ Kufar: {KufarWatcher.LastRaw[..Math.Min(400, KufarWatcher.LastRaw.Length)]}", ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Ошибка бота");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}

// Render усыпляет бесплатные веб-сервисы без входящего трафика; RENDER_EXTERNAL_URL задаётся Render автоматически
class SelfPinger(IHttpClientFactory httpFactory, ILogger<SelfPinger> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        if (string.IsNullOrEmpty(url))
        {
            log.LogInformation("RENDER_EXTERNAL_URL не задан, самопинг отключён");
            return;
        }

        var http = httpFactory.CreateClient();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
            try { await http.GetAsync($"{url.TrimEnd('/')}/health", ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Самопинг не удался");
            }
        }
    }
}
