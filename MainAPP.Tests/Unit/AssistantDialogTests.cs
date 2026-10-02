using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using MainAPP.Resources;
using MainAPP.Models;
using MainAPP.Services;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class AssistantDialogTests
{
    [Fact]
    public void Prompt_LocksFormulas_AndRefusesActions()
    {
        Assert.Equal("你是这块生产看板的问答助手。需要数据时自己调用工具。", AssistantPrompt.System);
        Assert.DoesNotContain("不要写 PLC", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("不要确认报警", AssistantPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_SendsTheQuestionWithoutThePage()
    {
        var from = new DateTime(2026, 9, 25, 8, 0, 0);
        var to = new DateTime(2026, 10, 1, 20, 0, 0);
        var text = AssistantPrompt.User(
            new AssistantPromptContext("HistoryQuery", "历史查询", "3号机", from, to, ["峰值 120 件"]),
            "早上是不是又停过");

        Assert.Equal("问题：早上是不是又停过", text);
        Assert.DoesNotContain("历史查询", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3号机", text, StringComparison.Ordinal);
        Assert.DoesNotContain("当前页面", text, StringComparison.Ordinal);
        Assert.DoesNotContain("峰值 120 件", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_KeepsThePageLeftBehind()
    {
        var store = new AssistantContextStore(() => "3号机");
        store.NotePage(NavigationPageCatalog.HistoryQuery.Key);
        store.NotePage(AssistantContextStore.PageKey);
        store.BindHistory(() => new AssistantHistoryFacts(
            new DateTime(2026, 9, 25),
            new DateTime(2026, 10, 1),
            ["最长报警 30 分钟"]));

        var context = store.Capture();

        Assert.Equal(NavigationPageCatalog.HistoryQuery.Key, context.PageKey);
        Assert.Equal("3号机", context.DeviceName);
        Assert.Contains("最长报警 30 分钟", context.Notes);
    }

    [Fact]
    public void Context_IncludesNamesAndManual_DropsAddresses()
    {
        var store = new AssistantContextStore(
            () => "3号机",
            () => ["3号机", "D100", "5号机"],
            _ => "这一页回看已经写入的记录。\nServer=secret");
        store.NotePage(NavigationPageCatalog.HistoryQuery.Key);

        var context = store.Capture();
        var text = AssistantPrompt.User(context, "划痕是不是3号机");

        Assert.Equal("问题：划痕是不是3号机", text);
        Assert.DoesNotContain("5号机", text, StringComparison.Ordinal);
        Assert.DoesNotContain("这一页回看已经写入的记录", text, StringComparison.Ordinal);
        Assert.DoesNotContain("D100", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FixedQuestions))]
    public void FixedQuestion_KeepsGivenNumbers_AndDoesNotInsertOthers(string question, string note, string present, string absent)
    {
        var text = AssistantPrompt.User(
            new AssistantPromptContext(
                "HistoryQuery",
                "历史查询",
                "3号机",
                new DateTime(2026, 9, 25, 8, 0, 0),
                new DateTime(2026, 10, 1, 20, 0, 0),
                [note],
                ["3号机", "5号机"]),
            question);

        Assert.Equal("问题：" + question, text);
        Assert.DoesNotContain(note, text, StringComparison.Ordinal);
        Assert.DoesNotContain(absent, text, StringComparison.Ordinal);
        _ = present;
        Assert.DoesNotContain("不要写 PLC", AssistantPrompt.System, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> FixedQuestions()
    {
        yield return new object[] { "早上那台是不是又停过", "最长报警 30 分钟", "30 分钟", "良品率 99%" };
        yield return new object[] { "划痕是不是3号机", "报警 高温 2 次", "3号机", "D100" };
        yield return new object[] { "良品率为什么是黄的", "良品率 92%", "92%", "良品率 96%" };
        yield return new object[] { "开动率怎么来的", "开动率 80%", "80%", "开动率 100%" };
        yield return new object[] { "性能率达标了吗", "性能率 70%", "70%", "性能率 110%" };
        yield return new object[] { "这一班产量多少", "总产量 405 件", "405 件", "总产量 1000 件" };
        yield return new object[] { "和上一班差多少", "比上一班少 20 件", "20 件", "少 200 件" };
        yield return new object[] { "报警确认一下", "高温未恢复", "高温未恢复", "已确认报警" };
        yield return new object[] { "把工单停了", "工单 WO-1 进行中", "WO-1", "工单已停止" };
        yield return new object[] { "配方下发了吗", "配方未在事实中", "配方未在事实中", "配方已下发" };
        yield return new object[] { "写一段交班", "良品率 92%", "92%", "良品率 100%" };
        yield return new object[] { "哪些事碰在一起", "8 点良品率下降", "8 点", "根因是换型" };
    }

    [Fact]
    public async Task ChatClient_ReadsTheReplyText()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"沿用页面上的良品率"}}]}""");
        var client = new LocalLlamaChatClient(handler);

        var reply = await client.CompleteAsync(new Uri("http://127.0.0.1:9/"), AssistantPrompt.System, "问题：看一下");

        Assert.Equal("沿用页面上的良品率", reply);
        Assert.DoesNotContain("\"max_tokens\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"temperature\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_UsesTheCarriedFacts_AndDoesNotStartUntilAsked()
    {
        var store = new AssistantContextStore(() => "3号机");
        store.NotePage(NavigationPageCatalog.HistoryQuery.Key);
        var host = new FakeHost();
        var chat = new FakeChat();
        var vm = new AssistantViewModel(host, chat, store);

        Assert.False(host.Started);
        vm.Draft = "早上是不是又停过";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(host.Started);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("早上是不是又停过", vm.Messages[0].Text);
        Assert.Contains("问题：早上是不是又停过", chat.User, StringComparison.Ordinal);
        Assert.Contains(NavigationPageCatalog.HistoryQuery.NavItem.AccessibleName, chat.User, StringComparison.Ordinal);
        Assert.Contains("3号机", chat.User, StringComparison.Ordinal);
        Assert.Contains(Strings.Assistant_ContextRangeNone, chat.User, StringComparison.Ordinal);
        Assert.True(chat.User.IndexOf("3号机", StringComparison.Ordinal) < chat.User.IndexOf("问题：", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_CallsTheOutputTool_AndKeepsTheCalculatedSentenceThere()
    {
        var host = new FakeHost();
        var chat = new ToolThenTextChat("query_output", "沿用页面上的数");
        var broker = new ScriptBroker("已经算好。数字只采用这一句，不要改合计：合格 10 件，不良 2 件，合计 12 件。");
        var vm = new AssistantViewModel(host, chat, new AssistantContextStore(() => null), tools: broker);
        vm.Draft = "昨天的合格率";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(host.Started);
        Assert.Equal("query_output", broker.Names[0]);
        Assert.Contains("合格 10 件", chat.ToolResult, StringComparison.Ordinal);
        Assert.DoesNotContain("合格 10 件", chat.User, StringComparison.Ordinal);
        Assert.Equal("沿用页面上的数", vm.Messages[1].Text);
        Assert.Equal("", vm.Messages[1].Notice);
    }

    [Fact]
    public async Task Send_StreamsTheReplyIntoTheTurn()
    {
        var vm = new AssistantViewModel(new FakeHost(), new ChunkChat(), new AssistantContextStore(() => null));
        vm.Draft = "看一下";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("沿用", vm.Messages[1].Text);
    }

    [Fact]
    public async Task Send_CarriesTheWholeConversation()
    {
        var chat = new FakeChat();
        var vm = new AssistantViewModel(new FakeHost(), chat, new AssistantContextStore(() => null));
        vm.Draft = "今天几件";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Draft = "说短一点";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Draft = "再说一遍";
        await vm.SendCommand.ExecuteAsync(null);

        var sent = string.Join("\n", chat.Sent.Select(message => message.Role + ":" + message.Content));
        var first = sent.IndexOf("问题：今天几件", StringComparison.Ordinal);
        var second = sent.IndexOf("问题：说短一点", StringComparison.Ordinal);
        var third = sent.IndexOf("问题：再说一遍", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third);
        Assert.Equal(2, chat.Sent.Count(message => message.Role == "assistant" && message.Content == "沿用页面上的数"));
    }

    [Fact]
    public void Prompt_LeavesPageNamesAndManualOut()
    {
        var names = Enumerable.Range(0, 30).Select(i => new string('名', 40) + i).ToList();
        var manual = new string('册', 13000);
        var piece = AssistantPrompt.Write(
            new AssistantPromptContext("Home", "首页", "甲", null, null, [], names, manual),
            "哪台在拖后腿");

        Assert.Equal("问题：哪台在拖后腿", piece.Text);
        Assert.False(piece.DroppedNames);
        Assert.False(piece.DroppedManual);
        Assert.False(piece.DroppedDetails);
    }

    [Fact]
    public void Prompt_KeepsEarlierTurnsAndDropsTheOldestWhenFull()
    {
        var history = AssistantPrompt.History(
            [(true, "今天几件"), (false, "合格 12 件"), (true, "说短一点"), (false, "12 件")],
            "再说一遍");

        Assert.Equal("问题：今天几件", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("问题：再说一遍", history[^1].Content);

        var bulky = new string('早', AssistantPrompt.MaxUserChars);
        var trimmed = AssistantPrompt.History(
            [(true, bulky), (false, "旧回答"), (true, "昨天几件"), (false, "合格 3 件")],
            "今天呢");

        Assert.DoesNotContain(trimmed, message => message.Content != null && message.Content.Contains(bulky, StringComparison.Ordinal));
        Assert.Contains(trimmed, message => message.Content == "问题：昨天几件");
        Assert.Equal("问题：今天呢", trimmed[^1].Content);

        var withContext = AssistantPrompt.History([], "有哪些工单", "上一页：报警中心    设备：注塑机A1    查询时间：还没有打开历史查询");
        Assert.Equal("上一页：报警中心    设备：注塑机A1    查询时间：还没有打开历史查询\n问题：有哪些工单", withContext[0].Content);
    }

    [Fact]
    public async Task ChatClient_StreamsDeltas()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"沿\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"用\"}}]}\n\ndata: [DONE]\n";
        var handler = new RecordingHandler(sse);
        var client = new LocalLlamaChatClient(handler);
        var parts = new List<string>();
        await foreach (var part in client.StreamAsync(new Uri("http://127.0.0.1:9/"), "系统", "问题"))
            parts.Add(part);

        Assert.Equal(["沿", "用"], parts);
        Assert.Contains("\"stream\":true", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerCheck_FlagsNumbersMissingFromFacts()
    {
        Assert.False(AssistantAnswerCheck.HasNumberOutside("合格 12 件", ["合格 12 件，不良 0 件"]));
        Assert.False(AssistantAnswerCheck.HasNumberOutside("大约 12.0 件", ["合格 12 件"]));
        Assert.False(AssistantAnswerCheck.HasNumberOutside("没有具体件数", ["合格 12 件"]));
        Assert.True(AssistantAnswerCheck.HasNumberOutside("合格 99 件", ["合格 12 件"]));
    }

    [Fact]
    public async Task Send_MarksANumberThatIsNotInTheFacts()
    {
        var vm = new AssistantViewModel(
            new FakeHost(),
            new FixedReplyChat("合格 99 件"),
            new AssistantContextStore(() => null));
        vm.Draft = "今天有哪些报警";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("合格 99 件", vm.Messages[1].Text);
        Assert.Equal("", vm.Messages[1].Notice);
    }

    [Fact]
    public async Task Send_LoadsAlarmsThroughTheTool()
    {
        var chat = new ToolThenTextChat("list_alarms", "温度过高还没恢复");
        var broker = new ScriptBroker("报警记录 注塑机A1 温度过高。");
        var vm = new AssistantViewModel(new FakeHost(), chat, new AssistantContextStore(() => "甲"), tools: broker);
        vm.Draft = "今天有哪些报警";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["list_alarms"], broker.Names);
        Assert.Contains("温度过高", chat.ToolResult, StringComparison.Ordinal);
        Assert.DoesNotContain("温度过高", chat.User, StringComparison.Ordinal);
        Assert.Equal("温度过高还没恢复", vm.Messages[1].Text);
        Assert.Equal("", vm.Messages[1].Notice);
    }

    [Fact]
    public void ToolXml_ReadsTheFunctionTheTemplateEmits()
    {
        var calls = AssistantToolXml.ReadCalls(
            "<function name=\"list_alarms\"><param name=\"period\">昨天</param><param name=\"device\">这台</param></function>");

        var call = Assert.Single(calls);
        Assert.Equal("list_alarms", call.Name);
        using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("昨天", document.RootElement.GetProperty("period").GetString());
        Assert.Equal("这台", document.RootElement.GetProperty("device").GetString());
    }

    [Fact]
    public void MetricQuestion_KeepsTheAskedPeriod()
    {
        Assert.Equal("昨天的产量", AssistantToolQuestions.ForMetric("query_output", "昨天", "", "昨天的合格率"));
        Assert.Equal("注塑机A1今天的报警时长", AssistantToolQuestions.ForMetric("query_alarm_duration", "今天", "注塑机A1", "报警多久"));
        Assert.Equal("前天的合格率", AssistantToolQuestions.ForMetric("query_output", "", "", "前天的合格率"));
    }

    [Fact]
    public async Task ChatClient_SendsToolsAndReadsEmbeddedFunction()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"<function name=\\\"list_alarms\\\"><param name=\\\"period\\\">今天</param></function>\"}}]}\n\ndata: [DONE]\n";
        var handler = new RecordingHandler(sse);
        var client = new LocalLlamaChatClient(handler);
        var deltas = new List<AssistantChatDelta>();
        await foreach (var delta in client.StreamRoundAsync(
            new Uri("http://127.0.0.1:9/"),
            [new AssistantChatMessage("system", "系统"), new AssistantChatMessage("user", "今天有哪些报警")],
            AssistantToolCatalog.All,
            true))
            deltas.Add(delta);

        Assert.Contains("\"name\":\"query_output\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"tool_choice\":\"auto\"", handler.Body, StringComparison.Ordinal);
        var call = Assert.Single(deltas).ToolCalls![0];
        Assert.Equal("list_alarms", call.Name);
        using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("今天", document.RootElement.GetProperty("period").GetString());
    }

    [Fact]
    public async Task Send_StopsCallingToolsAndAnswers()
    {
        var chat = new AlwaysToolChat();
        var broker = new ScriptBroker("报警记录 注塑机A1 温度过高。");
        var vm = new AssistantViewModel(new FakeHost(), chat, new AssistantContextStore(() => null), tools: broker);
        vm.Draft = "今天有哪些报警";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(AssistantViewModel.MaxToolRounds, broker.Names.Count);
        Assert.True(chat.AskedToAnswer);
        Assert.Equal("查完了", vm.Messages[1].Text);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Send_StopKeepsThePartialAnswer()
    {
        var chat = new HoldChat();
        var vm = new AssistantViewModel(new FakeHost(), chat, new AssistantContextStore(() => null));
        vm.Draft = "看一下";
        var send = vm.SendCommand.ExecuteAsync(null);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (vm.Messages.Count < 2 || vm.Messages[1].Text.Length == 0)
        {
            if (DateTime.UtcNow > until)
                break;
            await Task.Delay(20);
        }

        vm.StopCommand.Execute(null);
        await send;

        Assert.Equal("已经", vm.Messages[1].Text);
        Assert.Equal(Strings.Assistant_Stopped, vm.Status);
    }

    [Fact]
    public void Tally_PromotesTheThirdAsk_AndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "kanban-assistant-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var tally = new AssistantQuestionTally(path);
            tally.Record("哪台在拖后腿");
            tally.Record("  哪台在拖后腿 ");
            Assert.Empty(tally.Repeated());
            tally.Record("哪台在拖后腿");

            Assert.Equal(["哪台在拖后腿"], tally.Repeated());
            Assert.Equal(["哪台在拖后腿"], new AssistantQuestionTally(path).Repeated());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RecordingHandler(string response) => _response = response;

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeHost : ILocalLlamaHost
    {
        public bool Started { get; private set; }

        public Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.FromResult(new Uri("http://127.0.0.1:9/"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptBroker : IAssistantToolBroker
    {
        private readonly string _result;

        public ScriptBroker(string result) => _result = result;

        public List<string> Names { get; } = [];

        public IReadOnlyList<AssistantToolSpec> Tools => AssistantToolCatalog.All;

        public string Execute(string name, string argumentsJson, DateTime now, string? selectedDeviceName, string userQuestion, CancellationToken cancellationToken)
        {
            Names.Add(name);
            return _result;
        }
    }

    private sealed class AlwaysToolChat : ILocalLlamaChatClient
    {
        public bool AskedToAnswer { get; private set; }

        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
            => Task.FromResult("查完了");

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "查完了";
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!allowTools)
            {
                AskedToAnswer = messages.Any(message => message.Content?.Contains("不要再调用函数", StringComparison.Ordinal) == true);
                yield return new AssistantChatDelta("查完了", null);
                yield break;
            }

            yield return new AssistantChatDelta(null, [new AssistantToolCall("c1", "list_alarms", "{}")]);
            await Task.CompletedTask;
        }
    }

    private sealed class ToolThenTextChat : ILocalLlamaChatClient
    {
        private readonly string _tool;
        private readonly string _reply;
        private int _round;

        public ToolThenTextChat(string tool, string reply)
        {
            _tool = tool;
            _reply = reply;
        }

        public string User { get; private set; } = "";

        public string ToolResult { get; private set; } = "";

        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
            => Task.FromResult(_reply);

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _reply;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            User = messages.First(message => message.Role == "user").Content ?? "";
            if (_round++ == 0)
            {
                yield return new AssistantChatDelta(null, [new AssistantToolCall("c1", _tool, "{\"period\":\"昨天\"}")]);
                yield break;
            }

            ToolResult = messages.Last(message => message.Role == "tool").Content ?? "";
            yield return new AssistantChatDelta(_reply, null);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeChat : ILocalLlamaChatClient
    {
        public string User { get; private set; } = "";

        public IReadOnlyList<AssistantChatMessage> Sent { get; private set; } = [];

        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
        {
            User = user;
            return Task.FromResult("沿用页面上的数");
        }

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            User = user;
            yield return "沿用页面上的数";
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = messages.ToList();
            User = messages.Last(message => message.Role == "user").Content ?? "";
            yield return new AssistantChatDelta("沿用页面上的数", null);
            await Task.CompletedTask;
        }
    }

    private sealed class FixedReplyChat : ILocalLlamaChatClient
    {
        private readonly string _reply;

        public FixedReplyChat(string reply) => _reply = reply;

        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
            => Task.FromResult(_reply);

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _reply;
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AssistantChatDelta(_reply, null);
            await Task.CompletedTask;
        }
    }

    private sealed class HoldChat : ILocalLlamaChatClient
    {
        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
            => Task.FromResult("已经");

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "已经";
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AssistantChatDelta("已经", null);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ChunkChat : ILocalLlamaChatClient
    {
        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
            => Task.FromResult("沿用");

        public async IAsyncEnumerable<string> StreamAsync(
            Uri endpoint,
            string system,
            string user,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "沿";
            yield return "用";
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AssistantChatDelta> StreamRoundAsync(
            Uri endpoint,
            IReadOnlyList<AssistantChatMessage> messages,
            IReadOnlyList<AssistantToolSpec> tools,
            bool allowTools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AssistantChatDelta("沿", null);
            yield return new AssistantChatDelta("用", null);
            await Task.CompletedTask;
        }
    }
}
