using System.CommandLine;
using System.IO;

namespace LicenseIssuer;

/// <summary>
/// 激活码签发工具（CLI）。
/// 内网管理员机器运行，使用 LicenseManager.App 同一份 HMAC 密钥生成激活码。
/// </summary>
/// <remarks>
/// 用法示例：
///   LicenseIssuer.exe issue --machine ABCD1234                    # 永久授权，绑定机器码
///   LicenseIssuer.exe issue --machine ABCD1234 --expire 2026-12-31 # 限期授权
///   LicenseIssuer.exe list                                         # 查看所有签发/撤销记录
///   LicenseIssuer.exe list --machine ABCD1234                      # 按机器码过滤
///   LicenseIssuer.exe revoke --machine ABCD1234                    # 撤销指定机器码的所有签发
///   LicenseIssuer.exe revoke --machine ABCD1234 --reason "测试机"  # 撤销并记录原因
///   LicenseIssuer.exe verify --key XXXXX-XXXXX-XXXXX-XXXXX-XXXXX              # 验证激活码签名/格式
///   LicenseIssuer.exe verify --key XXXXX-XXXXX-XXXXX-XXXXX-XXXXX --machine ABCD1234  # 同时校验机器绑定
/// </remarks>
internal class Program
{
    private const string IssuedDir = "issued";

    static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(IssuedDir);

        var rootCommand = new RootCommand("激活码签发工具 - 离线授权管理");

        // ──────────── issue 命令 ────────────
        var machineOption = new Option<string>(
            name: "--machine",
            description: "目标机器码（8 字符，从客户端激活对话框获取）")
        { IsRequired = true };

        var expireOption = new Option<DateTime?>(
            name: "--expire",
            description: "过期日期（YYYY-MM-DD），不指定则为永久授权");

        var issueCommand = new Command("issue", "签发新激活码");
        issueCommand.AddOption(machineOption);
        issueCommand.AddOption(expireOption);
        issueCommand.SetHandler((machine, expire) => IssueCommand.Handle(machine, expire), machineOption, expireOption);

        // ──────────── list 命令（支持 --machine 过滤）────────────
        var listMachineOption = new Option<string?>(
            name: "--machine",
            description: "按机器码过滤（8 字符），不指定则列出所有记录");

        var listCommand = new Command("list", "查看已签发/已撤销的激活码记录");
        listCommand.AddOption(listMachineOption);
        listCommand.SetHandler(machine => ListCommand.Handle(machine), listMachineOption);

        // ──────────── revoke 命令 ────────────
        var revokeMachineOption = new Option<string>(
            name: "--machine",
            description: "要撤销的机器码（8 字符）")
        { IsRequired = true };

        var reasonOption = new Option<string?>(
            name: "--reason",
            description: "撤销原因（可选，记录到撤销日志）");

        var revokeCommand = new Command("revoke", "撤销指定机器码的签发记录");
        revokeCommand.AddOption(revokeMachineOption);
        revokeCommand.AddOption(reasonOption);
        revokeCommand.SetHandler((machine, reason) => RevokeCommand.Handle(machine, reason), revokeMachineOption, reasonOption);

        // ──────────── verify 命令（U-2）────────────
        var keyOption = new Option<string>(
            name: "--key",
            description: "待验证的激活码（XXXXX-XXXXX-XXXXX-XXXXX-XXXXX）")
        { IsRequired = true };

        var verifyMachineOption = new Option<string?>(
            name: "--machine",
            description: "可选：校验激活码是否绑定到指定机器码（8 字符）");

        var verifyCommand = new Command("verify", "验证激活码的签名、机器绑定、过期及撤销状态");
        verifyCommand.AddOption(keyOption);
        verifyCommand.AddOption(verifyMachineOption);
        verifyCommand.SetHandler((key, machine) => VerifyCommand.Handle(key, machine), keyOption, verifyMachineOption);

        rootCommand.AddCommand(issueCommand);
        rootCommand.AddCommand(listCommand);
        rootCommand.AddCommand(revokeCommand);
        rootCommand.AddCommand(verifyCommand);

        return await rootCommand.InvokeAsync(args);
    }
}
