using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

// Render передаёт порт через PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddHostedService<KufarWatcher>();
builder.Services.AddHostedService<SelfPinger>();

var app = builder.Build();
app.MapGet("/", () => "FlatSearcher is running");
app.MapGet("/health", () => Results.Ok());
app.Run();

class KufarWatcher(IHttpClientFactory httpFactory, ILogger<KufarWatcher> log) : BackgroundService
{
    const string KufarUrl =
        "https://api.kufar.by/search-api/v2/search/rendered-paginated" +
        "?cat=1010&typ=let&cur=USD&lang=ru&size=30&sort=lst.d" +
        "&gtsy=country-belarus~province-mogilevskaja_oblast~locality-bobrujsk";

    static readonly string BotToken = Environment.GetEnvironmentVariable("TG_BOT_TOKEN") ?? "";
    static readonly string ChatId = Environment.GetEnvironmentVariable("TG_CHAT_ID") ?? "";
    static readonly int IntervalSec = int.TryParse(Environment.GetEnvironmentVariable("POLL_INTERVAL"), out var i) ? i : 60;
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
                var json = JsonNode.Parse(await http.GetStringAsync(KufarUrl, ct));
                var ads = json?["ads"]?.AsArray().ToList() ?? [];
                var fresh = ads.Where(a => !seen.Contains(a!["ad_id"]!.ToString())).ToList();

                if (firstRun)
                {
                    fresh.Clear(); // при первом запуске только запоминаем текущие
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

    static string Format(JsonNode ad)
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

    static async Task Notify(HttpClient http, string text, CancellationToken ct)
    {
        var resp = await http.PostAsync(
            $"https://api.telegram.org/bot{BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = ChatId, ["text"] = text }),
            ct);
        resp.EnsureSuccessStatusCode();
    }

    static HashSet<string>? LoadSeen()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StateFile)); }
        catch (FileNotFoundException) { return null; }
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
