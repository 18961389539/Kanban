using System.Net;
using System.Net.Http;
using System.Text;
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
        Assert.Contains("良品率 = 合格数 / (合格数 + 不良数)", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.Contains("不要确认报警", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.Contains("不要下发配方", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.Contains("不要写 PLC", AssistantPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_CarriesGivenFacts_Only()
    {
        var from = new DateTime(2026, 9, 25, 8, 0, 0);
        var to = new DateTime(2026, 10, 1, 20, 0, 0);
        var text = AssistantPrompt.User(
            new AssistantPromptContext("HistoryQuery", "历史查询", "3号机", from, to, ["峰值 120 件"]),
            "早上是不是又停过");

        Assert.Contains("历史查询", text, StringComparison.Ordinal);
        Assert.Contains("3号机", text, StringComparison.Ordinal);
        Assert.Contains("2026-09-25 08:00", text, StringComparison.Ordinal);
        Assert.Contains("峰值 120 件", text, StringComparison.Ordinal);
        Assert.Contains("早上是不是又停过", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", text, StringComparison.Ordinal);
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

        Assert.Contains("3号机", text, StringComparison.Ordinal);
        Assert.Contains("5号机", text, StringComparison.Ordinal);
        Assert.Contains("这一页回看已经写入的记录", text, StringComparison.Ordinal);
        Assert.Contains("不是本班的数", text, StringComparison.Ordinal);
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

        Assert.Contains(question, text, StringComparison.Ordinal);
        Assert.Contains(present, text, StringComparison.Ordinal);
        Assert.DoesNotContain(absent, text, StringComparison.Ordinal);
        Assert.Contains("不要确认报警", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.Contains("不要下发配方", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.Contains("不要写 PLC", AssistantPrompt.System, StringComparison.Ordinal);
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
        Assert.Contains("max_tokens", handler.Body, StringComparison.Ordinal);
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
        Assert.Contains(NavigationPageCatalog.HistoryQuery.NavItem.AccessibleName, chat.User, StringComparison.Ordinal);
        Assert.Contains("3号机", chat.User, StringComparison.Ordinal);
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

    private sealed class FakeChat : ILocalLlamaChatClient
    {
        public string User { get; private set; } = "";

        public Task<string> CompleteAsync(Uri endpoint, string system, string user, CancellationToken cancellationToken = default)
        {
            User = user;
            return Task.FromResult("沿用页面上的数");
        }
    }
}
