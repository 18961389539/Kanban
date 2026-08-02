using LicenseManager.Models;

namespace LicenseManager.Services;

/// <summary>
/// TrialRegistryBackup 的内存版本，用于单元测试。
/// 不写入真实注册表，避免污染测试机器。
/// </summary>
public class TrialRegistryBackupStub : TrialRegistryBackup
{
    private TrialState? _storedState;

    public override TrialState? LoadBackupState() => _storedState;

    public override bool TrySaveBackupState(TrialState state)
    {
        _storedState = state;
        return true;
    }

    /// <summary>测试辅助：模拟注册表无记录</summary>
    public void Clear() => _storedState = null;
}
