using Spms.Tests;

Console.WriteLine("SpMS backend suite\n==================");

DomainTests.Run();

// Boot the real host and drive it over HTTP.
var app = Spms.Api.HostFactory.Build(["--urls", "http://127.0.0.1:5199"]);
_ = app.RunAsync();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var up = false;
for (var i = 0; i < 60 && !up; i++)
{
    try { up = (await http.GetAsync("http://127.0.0.1:5199/health")).IsSuccessStatusCode; }
    catch { await Task.Delay(250); }
}

if (!up)
{
    Harness.Check("API host started", false, "no response on /health after 15s");
}
else
{
    await HttpTests.RunAsync(http);
}


await app.StopAsync();
return Harness.Summarise();
