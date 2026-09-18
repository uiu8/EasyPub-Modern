using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>
/// 交互日志 —— 每次用户动作与关键决策写一行 JSON，供事后复盘。
///
/// <para><b>它存在的理由</b>：这个软件的界面结论一直只能靠截图与断言验证，而截图看不到
/// "用户点了什么、代码因此走了哪条分支、结果是什么"。走查时用户描述的和实际发生的之间
/// 隔着一层猜测。有了这份日志，走一遍就能读到完整轨迹，不必再问"你刚才点的是哪个"。</para>
///
/// <para><b>它永远不该影响产品行为</b>：写失败就静默放弃（磁盘满、目录只读、并发写），
/// 不抛异常、不阻塞、不改任何返回值。<see cref="Disable"/> 之后所有调用都是空操作。</para>
///
/// <para>默认位置：<c>%LOCALAPPDATA%\EasyPub Modern\interaction.jsonl</c>；
/// 环境变量 <c>EASYPUB_INTERACTION_LOG</c> 可覆盖（测试用它写到临时目录）。</para>
/// </summary>
public static class InteractionLog
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;
    private static bool _disabled;

    /// <summary>日志文件路径；<see cref="Disable"/> 之后为 null。</summary>
    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                if (_disabled) return null;
                return _path ??= Environment.GetEnvironmentVariable("EASYPUB_INTERACTION_LOG") is { Length: > 0 } custom
                    ? System.IO.Path.GetFullPath(custom)
                    : System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "EasyPub Modern", "interaction.jsonl");
            }
        }
    }

    /// <summary>关掉日志（测试与批量场景用；关掉之后所有调用都是空操作）。</summary>
    public static void Disable()
    {
        lock (Gate) _disabled = true;
    }

    /// <summary>
    /// 记一次用户动作。<paramref name="detail"/> 里放能让读者还原现场的东西
    /// （按钮文字、问题码、选中了什么），不要放整份文档。
    /// </summary>
    public static void User(string action, object? detail = null) => Write("user", action, detail);

    /// <summary>
    /// 记一次**决策**：代码在岔路口选了哪条。这是这份日志最有价值的部分 ——
    /// 界面上看到的结果对不对，取决于这里选得对不对。
    /// </summary>
    public static void Decision(string action, object? detail = null) => Write("decision", action, detail);

    /// <summary>记一次**结果**：动作完成了什么（数量、耗时、成败原因）。</summary>
    public static void Outcome(string action, object? detail = null) => Write("outcome", action, detail);

    private static void Write(string kind, string action, object? detail)
    {
        string? path;
        lock (Gate)
        {
            if (_disabled) return;
            path = Path;
        }
        if (path is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new Entry(
                DateTimeOffset.Now.ToString("O"), Clock.ElapsedMilliseconds, kind, action, Detail: detail));
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                // 追加一行：并发写靠 FileShare 兜住，两条日志交错也比丢一条好。
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                stream.Write(bytes);
            }
        }
        // 日志绝不能让产品失败：磁盘满、目录只读、被占用 —— 都当没发生过。
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or JsonException)
        {
        }
    }

    private sealed record Entry(string Ts, long Ms, string Kind, string Action, object? Detail);
}
