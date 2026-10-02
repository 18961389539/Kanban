using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class AssistantFactSheetTests
{
    [Fact]
    public void Prompt_LeavesTheQuestionToTheModel()
    {
        Assert.Equal("你是这块生产看板的问答助手。需要数据时自己调用工具。", AssistantPrompt.System);
        Assert.DoesNotContain("不要写 PLC", AssistantPrompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("已经算好", AssistantPrompt.System, StringComparison.Ordinal);

        var text = AssistantPrompt.User(
            new AssistantPromptContext("AlarmCenter", "报警中心", "注塑机A1", null, null, []),
            "今天约生产了几个产品");
        Assert.Equal("问题：今天约生产了几个产品", text);
        Assert.DoesNotContain("报警中心", text, StringComparison.Ordinal);
        Assert.DoesNotContain("注塑机A1", text, StringComparison.Ordinal);
    }
}
