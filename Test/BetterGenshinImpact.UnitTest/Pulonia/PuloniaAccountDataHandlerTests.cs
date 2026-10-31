using BetterGenshinImpact.Pulonia.Models;
using BetterGenshinImpact.Pulonia.Services;

namespace BetterGenshinImpact.UnitTest.Pulonia;

/// <summary>
/// 账户目录与按 UID 存放数据的其他模块（如狛荷屋）的扩展点：保存前检查 UID 修改，被拒绝时不保存；
/// 保存后数据随 UID 改名、随账户删除，失败只作为警告返回，不回滚目录。用记录调用的替身代替真实模块。
/// </summary>
public sealed class PuloniaAccountDataHandlerTests : IDisposable
{
    /// <summary>本测试独占的目录，不接触用户真实账户与凭据。</summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bgi-" + BetterGenshinImpact.Helpers.ShortId.Create(null, 8));

    /// <summary>修改 UID 时先检查再保存，保存成功后通知改名，且在“账户目录已变化”事件之前完成。</summary>
    [Fact] public async Task UidChangeIsCheckedThenRenamedAfterSave()
    {
        using var store = new PuloniaTaskStore(_directory);
        var handler = new RecordingHandler(store);
        var accounts = new PuloniaAccountService(store, new PuloniaInstanceKeyProvider(PuloniaInstanceKey.Primary), [handler]);
        var settings = await AddAccountAsync(accounts, "乙", "100000002");
        var renamedBeforeEvent = false;
        accounts.AccountsChanged += (_, _) => renamedBeforeEvent = handler.Renamed.Count == 1;

        settings.Accounts[1].Uid = "100000003";
        var result = await accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>());

        Assert.Equal([("100000002", (string?)"100000003")], handler.Checked);
        Assert.Equal([("100000002", "100000003")], handler.Renamed);
        // 改名时目录已经保存为新 UID
        Assert.Equal(["100000003"], handler.SavedUidsAtRename);
        Assert.True(renamedBeforeEvent);
        Assert.Null(result.DataWarning);
        Assert.Empty(handler.Removed);
    }

    /// <summary>关联数据模块拒绝 UID 修改时抛出带账户名称的原因，账户目录不保存，也不通知改名。</summary>
    [Fact] public async Task RejectedUidChangeDoesNotSave()
    {
        using var store = new PuloniaTaskStore(_directory);
        var handler = new RecordingHandler { Rejection = "UID 100000003 已有测试数据，不能改为这个 UID。" };
        var accounts = new PuloniaAccountService(store, new PuloniaInstanceKeyProvider(PuloniaInstanceKey.Primary), [handler]);
        var settings = await AddAccountAsync(accounts, "乙", "100000002");
        var revision = settings.Revision;

        settings.Accounts[1].Uid = "100000003";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>()));

        Assert.Contains("乙", error.Message);
        Assert.Contains("已有测试数据", error.Message);
        var saved = await accounts.LoadAccountsAsync();
        Assert.Equal(revision, saved.Revision);
        Assert.Equal("100000002", saved.Accounts[1].Uid);
        Assert.Empty(handler.Renamed);
    }

    /// <summary>删除有 UID 的账户后通知删除它的数据；只改名称或服务器时不检查 UID。</summary>
    [Fact] public async Task RemovedAccountDataIsDeletedAfterSave()
    {
        using var store = new PuloniaTaskStore(_directory);
        var handler = new RecordingHandler();
        var accounts = new PuloniaAccountService(store, new PuloniaInstanceKeyProvider(PuloniaInstanceKey.Primary), [handler]);
        var settings = await AddAccountAsync(accounts, "乙", "100000002");
        settings.Accounts[1].Name = "丙";
        settings = (await accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>())).Settings;
        Assert.Empty(handler.Checked);

        settings.Accounts.RemoveAt(1);
        var result = await accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>());

        Assert.Equal(["100000002"], handler.Removed);
        Assert.Null(result.DataWarning);
        Assert.Single((await accounts.LoadAccountsAsync()).Accounts);
    }

    /// <summary>保存后改名失败时目录保持已保存，保存结果带警告，说明哪个账户的哪类数据未能处理。</summary>
    [Fact] public async Task RenameFailureIsReturnedAsWarning()
    {
        using var store = new PuloniaTaskStore(_directory);
        var handler = new RecordingHandler { RenameFailure = new IOException("文件被占用") };
        var accounts = new PuloniaAccountService(store, new PuloniaInstanceKeyProvider(PuloniaInstanceKey.Primary), [handler]);
        var settings = await AddAccountAsync(accounts, "乙", "100000002");

        settings.Accounts[1].Uid = "100000003";
        var result = await accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>());

        Assert.Equal("100000003", (await accounts.LoadAccountsAsync()).Accounts[1].Uid);
        Assert.NotNull(result.DataWarning);
        Assert.Contains("“乙”的测试数据", result.DataWarning);
        Assert.Contains("文件被占用", result.DataWarning);
    }

    /// <summary>在账户目录末尾加入一个有 UID 的账户并保存，返回保存后的目录。</summary>
    private static async Task<PuloniaGenshinAccountSettings> AddAccountAsync(IPuloniaAccountService accounts, string name, string uid)
    {
        var settings = await accounts.LoadAccountsAsync();
        settings.Accounts.Add(new PuloniaGenshinAccountProfile { Name = name, Uid = uid });
        return (await accounts.SaveAccountsAsync(settings, new Dictionary<string, (string, string)>())).Settings;
    }

    /// <summary>仅清理本测试创建的独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    /// <summary>记录各次调用的关联数据模块替身；可配置拒绝原因和改名失败。</summary>
    /// <param name="store">改名时读取磁盘上的账户目录；为空时不读取</param>
    private sealed class RecordingHandler(PuloniaTaskStore? store = null) : IPuloniaAccountDataHandler
    {
        /// <summary>检查时返回的拒绝原因；为空时允许修改。</summary>
        public string? Rejection { get; init; }

        /// <summary>改名时抛出的异常；为空时成功。</summary>
        public Exception? RenameFailure { get; init; }

        /// <summary>收到的检查请求。</summary>
        public List<(string OldUid, string? NewUid)> Checked { get; } = [];

        /// <summary>收到的改名通知。</summary>
        public List<(string OldUid, string NewUid)> Renamed { get; } = [];

        /// <summary>收到改名通知时磁盘上账户目录中的 UID，用来确认改名发生在保存之后。</summary>
        public List<string> SavedUidsAtRename { get; } = [];

        /// <summary>收到的删除通知。</summary>
        public List<string> Removed { get; } = [];

        /// <inheritdoc />
        public string DataName => "测试数据";

        /// <inheritdoc />
        public Task<string?> CheckUidChangeAsync(string oldUid, string? newUid, CancellationToken ct = default)
        {
            Checked.Add((oldUid, newUid));
            return Task.FromResult(Rejection);
        }

        /// <inheritdoc />
        public async Task OnUidChangedAsync(string oldUid, string newUid, CancellationToken ct = default)
        {
            if (RenameFailure is not null)
                throw RenameFailure;
            Renamed.Add((oldUid, newUid));
            if (store is not null)
                SavedUidsAtRename.AddRange((await store.LoadAccountSettingsAsync(ct)).Accounts.Select(account => account.Uid).OfType<string>());
        }

        /// <inheritdoc />
        public Task OnAccountRemovedAsync(string uid, CancellationToken ct = default)
        {
            Removed.Add(uid);
            return Task.CompletedTask;
        }
    }
}
