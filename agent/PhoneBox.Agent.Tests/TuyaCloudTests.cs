using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Smart Life plugs over the Tuya Cloud API, against a local fake that checks every signature.</summary>
public sealed class TuyaCloudTests
{
    private static TuyaConfig Settings(string secret = "secret") => new() { Region = "us", AccessId = "id", AccessSecret = secret };

    // expected values computed with tinytuya's signing steps, outside this code
    [Theory]
    [InlineData("", "GET", "/v1.0/token?grant_type=1", "", "3A0955D30829FF424CDE7A508D72DCD4D6C25196F5C0F566866AA52FEB633802")]
    [InlineData("tok", "POST", "/v1.0/iot-03/devices/dev1/commands", "{\"commands\":[{\"code\":\"switch_1\",\"value\":true}]}",
        "CB7B135CC2DFE1577515227AD0A9ED8F1DF312C588F8CAE64C142C6B458EECA3")]
    public void RequestsAreSignedAsTuyaExpects(string token, string method, string url, string body, string expected) =>
        Assert.Equal(expected, TuyaCloud.Sign("id", "secret", token, "1700000000000", method, url, body));

    [Fact]
    public async Task SwitchingSendsOneCommandToTheChosenPlugAndReusesTheToken()
    {
        await using var f = new FakeTuya();
        var cloud = new TuyaCloud(f.Endpoint);
        Assert.Null((await cloud.SwitchAsync("plug1/switch_1", false, Settings())).Error);
        Assert.Null((await cloud.SwitchAsync("plug1/switch_1", true, Settings())).Error);
        Assert.Equal(["plug1/switch_1=False", "plug1/switch_1=True"], f.Commands);
        Assert.Equal(1, f.Tokens);
    }

    [Fact]
    public async Task AnInvalidTokenIsRenewedOnce()
    {
        await using var f = new FakeTuya { RejectTokens = 1 };
        var cloud = new TuyaCloud(f.Endpoint);
        Assert.Null((await cloud.SwitchAsync("plug1/switch_1", true, Settings())).Error);
        Assert.Equal(2, f.Tokens);
        f.RejectTokens = 10;
        Assert.Contains("1010", (await cloud.SwitchAsync("plug1/switch_1", true, Settings())).Error);
        Assert.Equal(3, f.Tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plug1")]
    [InlineData("../token/x")]
    [InlineData("plug1/switch_1/commands")]
    [InlineData("plug1/switch_1\n")]
    public async Task AnInvalidSwitchIdSendsNothing(string id)
    {
        await using var f = new FakeTuya();
        var r = await new TuyaCloud(f.Endpoint).SwitchAsync(id, false, Settings());
        Assert.NotNull(r.Error);
        Assert.False(r.Submitted);
        Assert.Equal(0, f.Tokens);
    }

    [Fact]
    public async Task AWrongSecretIsAnErrorWithoutTheSecretAndSwitchesNothing()
    {
        await using var f = new FakeTuya();
        var r = await new TuyaCloud(f.Endpoint).SwitchAsync("plug1/switch_1", false, Settings("wrong-secret"));
        Assert.Contains("Access Secret", r.Error);
        Assert.DoesNotContain("wrong-secret", r.Error);
        Assert.False(r.Submitted);
        Assert.Empty(f.Commands);
    }

    [Fact]
    public async Task ARefusedCommandIsAnError()
    {
        await using var f = new FakeTuya { RefuseCommands = true };
        var r = await new TuyaCloud(f.Endpoint).SwitchAsync("plug1/switch_1", false, Settings());
        Assert.Contains("2001", r.Error);
        Assert.True(r.Submitted);
    }

    [Fact]
    public async Task TheListHasEverySwitchOfEveryPageAndNothingElse()
    {
        await using var f = new FakeTuya();
        var list = await new TuyaCloud(f.Endpoint).DevicesAsync(Settings());
        Assert.Equal([
            new TuyaSwitch("plug1/switch_1", "Charger", true),
            new TuyaSwitch("bare/switch_1", "No values", false),
            new TuyaSwitch("strip/switch_1", "Strip (switch_1)", true),
            new TuyaSwitch("strip/switch_usb1", "Strip (switch_usb1)", true),
        ], list);
        Assert.Empty(f.Commands);
    }

    [Fact]
    public async Task ListingWithoutCredentialsAnswersAnError()
    {
        await using var f = new FakeTuya();
        var r = JsonSerializer.SerializeToElement(await new TuyaCloud(f.Endpoint).ListAsync(new TuyaConfig()));
        Assert.NotNull(r.GetProperty("error").GetString());
        Assert.Equal(0, f.Tokens);
    }

    [Fact]
    public void ARedactedSecretIsKeptAndAnEmptyOneClearsIt()
    {
        var proposed = JsonSerializer.Deserialize<AgentConfig>("{\"tuya\":{\"accessId\":\"id\"}}", ConfigStore.ReadOpts)!;
        Assert.True(TuyaConfig.PreserveSecret(proposed, "stored"));
        Assert.Equal("stored", proposed.Tuya.AccessSecret);
        proposed.Tuya.AccessSecret = "";
        Assert.False(TuyaConfig.PreserveSecret(proposed, "stored"));
        Assert.Null(TuyaConfig.Normalize(proposed.Tuya).AccessSecret);
        Assert.Equal("us", TuyaConfig.Normalize(new TuyaConfig { Region = "https://evil.example" }).Region);
        Assert.Equal("us", TuyaConfig.Normalize(null).Region);
    }

    [Fact]
    public async Task TheGuardSwitchesAndRestoresThePlug()
    {
        await using var f = new FakeTuya();
        f.Configure("tuya", "plug1/switch_1", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Charge, "off"));
        f.Configure("tuya", "plug1/switch_1", enabled: false);
        await f.Check();
        Assert.Equal(["plug1/switch_1=False", "plug1/switch_1=True"], f.Commands);
        await f.Check();
        Assert.Equal(2, f.Commands.Length);
    }

    [Theory]
    [InlineData("shortcuts", "plug1/switch_1")]
    [InlineData("ha", "plug1/switch_1")]
    [InlineData("tuya", "strip/switch_1")]
    public async Task ReplacingTheMethodOrPlugRestoresTheOneCutOff(string method, string plug)
    {
        await using var f = new FakeTuya();
        f.Configure("tuya", "plug1/switch_1", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Charge, "off"));
        f.Configure(method, plug, enabled: true);
        await f.Check();
        Assert.Equal(["plug1/switch_1=False", "plug1/switch_1=True"], f.Commands);
    }

    [Theory]
    [InlineData("id", "")]
    [InlineData("", "secret")]
    public async Task RemovingTheCredentialsStillRestoresThePlug(string id, string secret)
    {
        await using var f = new FakeTuya();
        f.Configure("tuya", "plug1/switch_1", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Charge, "off"));
        f.Configure("tuya", "plug1/switch_1", enabled: true, id, secret);
        await f.Check();
        Assert.Equal(["plug1/switch_1=False", "plug1/switch_1=True"], f.Commands);
    }

    [Fact]
    public async Task AFailedRestoreIsTriedAgain()
    {
        await using var f = new FakeTuya();
        f.Configure("tuya", "plug1/switch_1", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Charge, "off"));
        f.Configure("tuya", "plug1/switch_1", enabled: false);
        f.RefuseCommands = true;
        await f.Check();
        f.RefuseCommands = false;
        await f.Check();
        Assert.Equal(["plug1/switch_1=False", "plug1/switch_1=True"], f.Commands);
    }

    [Fact]
    public async Task NoChosenPlugSwitchesNothing()
    {
        await using var f = new FakeTuya();
        f.Configure("tuya", "", enabled: true);
        Assert.Equal("no Smart Life plug set", await f.Guard.Switch(f.Charge, "off"));
        Assert.Equal(0, f.Tokens);
    }

    private sealed class FakeTuya : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-tuya-" + Guid.NewGuid().ToString("N"));
        private readonly HttpListener _server = TestPorts.Listen();
        private readonly ConfigStore _store;
        private readonly System.Threading.Timer _pushTimer;
        private readonly AudioControl _audio = new();
        private readonly Task _serve;
        private readonly List<string> _commands = [];
        private string _token = "";
        public volatile int Tokens;
        public volatile int RejectTokens;
        public volatile bool RefuseCommands;
        public ChargeGuard Guard { get; }
        public string Endpoint => _server.Prefixes.Single().TrimEnd('/');
        public ChargeConfig Charge => _store.Get().Charge;
        public string[] Commands { get { lock (_commands) return _commands.ToArray(); } }

        public FakeTuya()
        {
            Directory.CreateDirectory(_dir);
            _store = new ConfigStore(Path.Combine(_dir, "config.json"));
            var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "data.json"));
            _pushTimer = (System.Threading.Timer)typeof(PushStore).GetField("_saveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(push)!;
            Guard = new ChargeGuard(_store, new HomeAssistant(_store, NullLogger<HomeAssistant>.Instance), push, _audio,
                NullLogger<ChargeGuard>.Instance, new TuyaCloud(Endpoint));
            _serve = Serve();
        }

        public void Configure(string method, string plug, bool enabled, string id = "id", string secret = "secret")
        {
            var error = _store.Save(JsonSerializer.Serialize(new {
                tuya = new { region = "us", accessId = id, accessSecret = secret },
                charge = new { enabled, method, tuyaSwitch = plug },
            }));
            if (error is not null) throw new Exception(error);
        }

        public Task Check() => ((Task)typeof(ChargeGuard).GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Guard, null)!)
            .WaitAsync(TimeSpan.FromSeconds(8));

        private async Task Serve()
        {
            while (_server.IsListening)
            {
                HttpListenerContext context;
                try { context = await _server.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                using var reader = new StreamReader(context.Request.InputStream);
                var answer = Answer(context.Request, await reader.ReadToEndAsync());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer));
                context.Response.Close();
            }
        }

        private static string Fail(int code, string msg) => JsonSerializer.Serialize(new { success = false, code, msg });
        private static string Ok(string result) => "{\"success\":true,\"result\":" + result + "}";

        private string Answer(HttpListenerRequest req, string body)
        {
            string H(string n) => req.Headers[n] ?? "";
            if (H("secret") != "") return Fail(1, "the secret must not be sent");
            // Tuya signs the decoded query
            var url = Uri.UnescapeDataString(req.RawUrl!);
            if (H("client_id") != "id" || H("sign") != TuyaCloud.Sign("id", "secret", H("access_token"), H("t"), req.HttpMethod, url, body))
                return Fail(1004, "sign invalid");
            if (url == "/v1.0/token?grant_type=1")
            {
                _token = $"token{++Tokens}";
                return Ok($"{{\"access_token\":\"{_token}\",\"expire_time\":7200}}");
            }
            if (RejectTokens > 0 || H("access_token") != _token)
            {
                if (RejectTokens > 0) RejectTokens--;
                return Fail(1010, "token invalid");
            }
            if (url == "/v1.0/iot-01/associated-users/devices?size=50")
                return Ok("""
                    {"has_more":true,"last_row_key":"a+b/c=","devices":[
                      {"id":"strip","name":"Strip","category":"pc","online":true,"status":[
                        {"code":"switch_1","value":true},{"code":"switch_usb1","value":false},{"code":"countdown_1","value":0}]},
                      {"id":"sensor","name":"Sensor","category":"wsdcg","online":true,"status":[{"code":"va_temperature","value":210}]}]}
                    """);
            if (url == "/v1.0/iot-01/associated-users/devices?last_row_key=a+b/c=&size=50")
                return Ok("""
                    {"has_more":false,"devices":[
                      {"id":"plug1","name":"Charger","category":"cz","online":true,"status":[{"code":"switch_1","value":true},{"code":"child_lock","value":false}]},
                      {"id":"bare","name":"No values","category":"cz","online":false}]}
                    """);
            if (req.HttpMethod == "POST" && url.StartsWith("/v1.0/iot-03/devices/", StringComparison.Ordinal) && url.EndsWith("/commands", StringComparison.Ordinal))
            {
                if (RefuseCommands) return Fail(2001, "device is offline");
                using var doc = JsonDocument.Parse(body);
                var command = doc.RootElement.GetProperty("commands").EnumerateArray().Single();
                lock (_commands) _commands.Add($"{url.Split('/')[4]}/{command.GetProperty("code").GetString()}={command.GetProperty("value").GetBoolean()}");
                return Ok("true");
            }
            return Fail(1108, "uri path invalid");
        }

        public async ValueTask DisposeAsync()
        {
            _server.Close();
            await _serve.WaitAsync(TimeSpan.FromSeconds(5));
            await _pushTimer.DisposeAsync();
            Guard.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
    }
}
