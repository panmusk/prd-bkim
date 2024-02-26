using Flurl.Http;
using Microsoft.Playwright;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

internal class Program
{
    public static SemaphoreSlim Semaphore = new SemaphoreSlim(5, 5);
    private static async Task Main(string[] args)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://www.polskieradio.pl/18/4469");
        var cookieAcceptButton = page.GetByRole(AriaRole.Button, new() { Name = "Akceptuję", Exact = true });
        if (cookieAcceptButton != null)
            await cookieAcceptButton.ClickAsync();
        bool isAdding = true;
        ConcurrentBag<EpisodeData> urlStatuses = new ConcurrentBag<EpisodeData>();
        var pageNavigator = page.Locator("div.bPager.saturn-pager");
        var episodes = await GetEpisodesAsync(page);
        episodes.ForEach(urlStatuses.Add);//add first page items so concurrent download can begin
        var downloadTasks = new List<Task>();
        var task = Task.Run(() =>
        {
            while (isAdding || !urlStatuses.IsEmpty)
            {
                while (urlStatuses.TryTake(out EpisodeData episode))
                {
                    downloadTasks.Add(episode.DownloadAsync());
                }
            }
        });
        await Task.WhenAll(downloadTasks);
        //adding the whole rest
        for (int i = 2; i <= 51; i++)
        {
            await pageNavigator.GetByText(i.ToString(), new() { Exact = true }).ClickAsync();
            episodes = await GetEpisodesAsync(page);
            episodes.ForEach(urlStatuses.Add);
        }
        await page.CloseAsync();
        isAdding = false;
        task.Wait();
    }
    private static async Task<List<EpisodeData>> GetEpisodesAsync(IPage page)
    {
        await page.WaitForSelectorAsync("span.play");
        var buttons = (await page.Locator("span.play").AllAsync()).ToList();

        return buttons
            .Select(x => new
            {
                jsonString = x.GetAttributeAsync("data-media").Result,
                date = x.Locator("xpath=./following-sibling::span[@class='description']")
                .Locator("span.date")
                .InnerTextAsync().Result
            })
            .Select(x =>
            {
                var epData = JsonConvert.DeserializeObject<EpisodeData>(x.jsonString);
                epData.DateString = x.date;
                return epData;
            })
            .ToList();
    }
}
public partial class EpisodeData
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("file")]
    public string File { get; set; }

    [JsonProperty("provider")]
    public string Provider { get; set; }

    [JsonProperty("uid")]
    public Guid Uid { get; set; }

    [JsonProperty("length")]
    public long Length { get; set; }

    [JsonProperty("autostart")]
    public bool Autostart { get; set; }

    [JsonProperty("link")]
    public string Link { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("desc")]
    public string Desc { get; set; }

    [JsonProperty("advert")]
    public long Advert { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }
    public string DateString { get; set; }
    public DateTime Date
    {
        get
        {
            DateTime.TryParseExact(this.DateString, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date);
            return date;
        }
    }
    public string DateFormatted => this.Date.ToString("yyyy-MM-dd");
    public async Task DownloadAsync()
    {
        Program.Semaphore.Wait();
        try
        {
            string fileName = $"{this.DateFormatted} {Regex.Replace(HttpUtility.UrlDecode(this.Title), @"[\\/:*?""<>|]", "-")}.mp3";
            fileName = System.IO.File.Exists(fileName) ? fileName + Path.GetRandomFileName() + ".mp3" : fileName;
            System.Console.WriteLine($"[{System.Environment.CurrentManagedThreadId}] downloading {fileName}...");
            string url = $"https:{this.File}";
            await url
            .WithHeader("user-agent", "chromium")
            .DownloadFileAsync(@".", fileName);
        }
        finally
        {
            Program.Semaphore.Release();
        }
    }
}
