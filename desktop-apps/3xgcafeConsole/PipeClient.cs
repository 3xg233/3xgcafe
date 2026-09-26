using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThreeXGCafeConsole;

/// <summary>工具返回的状态/结果。</summary>
public sealed class ToolReply
{
    public bool Ok { get; init; }
    public string State { get; init; } = "";
    public string Detail { get; init; } = "";
    public Dictionary<string, string> Data { get; init; } = new();
}

/// <summary>
/// 命名管道客户端：每次请求新建连接、一条请求一条响应、随即断开。
/// 工具未运行 / 未实现通道时返回 null（调用方回退到进程级状态）。
/// </summary>
public static class PipeClient
{
    public static async Task<ToolReply?> QueryAsync(string pipeName, string cmd,
        Dictionary<string, string>? args = null, int timeoutMs = 700)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            return null;

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(timeoutMs, cts.Token).ConfigureAwait(false);

            var request = new Dictionary<string, object> { ["cmd"] = cmd };
            if (args != null)
                foreach (var kv in args)
                    request[kv.Key] = kv.Value;

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);

            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            Task<string?> readTask = reader.ReadLineAsync();
            Task done = await Task.WhenAny(readTask, Task.Delay(timeoutMs, cts.Token)).ConfigureAwait(false);
            if (done != readTask)
                return null;

            string? line = await readTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return null;

            using var doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string state = root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() ?? "" : "";
            string detail = root.TryGetProperty("detail", out var dt) && dt.ValueKind == JsonValueKind.String
                ? dt.GetString() ?? "" : "";

            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                foreach (var p in dataEl.EnumerateObject())
                    data[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? ""
                        : p.Value.ToString();

            return new ToolReply { Ok = ok, State = state, Detail = detail, Data = data };
        }
        catch
        {
            return null;
        }
    }
}
