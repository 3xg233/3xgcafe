using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenGuard.Ipc;

/// <summary>
/// 3xgcafe Console 状态/控制通道（命名管道）：一行 JSON 请求 → 一行 JSON 响应。
/// 面板每次连接只发一条请求、处理完立即断开；不连接时服务端几乎零开销。
/// 通道名约定：3xgcafe-&lt;工具Id&gt;（与面板 tools.json 里的 pipeName 对应）。
/// </summary>
public sealed class ToolIpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, IReadOnlyDictionary<string, string>, string> _handler;
    private readonly CancellationTokenSource _cts = new();

    public ToolIpcServer(string toolId, Func<string, IReadOnlyDictionary<string, string>, string> handler)
    {
        _pipeName = "3xgcafe-" + toolId;
        _handler = handler;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                string response = Handle(line);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单次请求失败（面板中途断开等）不影响后续
            }
        }
    }

    private string Handle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ToolIpc.Response(false, "error", "空请求", null);

        string cmd = "";
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ToolIpc.Response(false, "error", "请求必须是 JSON 对象", null);

            if (doc.RootElement.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String)
                cmd = c.GetString() ?? "";

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("cmd")) continue;
                args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? ""
                    : p.Value.ToString();
            }
        }
        catch
        {
            return ToolIpc.Response(false, "error", "请求格式错误", null);
        }

        try
        {
            return _handler(cmd, args);
        }
        catch (Exception ex)
        {
            return ToolIpc.Response(false, "error", ex.Message, null);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>响应构造工具（各工具共用同一报文格式，协议版本 ver=1）。</summary>
public static class ToolIpc
{
    public static string Response(bool ok, string state, string detail, IReadOnlyDictionary<string, string>? data)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["state"] = state,
            ["detail"] = detail,
            ["ver"] = 1
        };
        if (data is { Count: > 0 })
            payload["data"] = data;

        return JsonSerializer.Serialize(payload);
    }

    public static bool TryGetInt(IReadOnlyDictionary<string, string> args, string key, out int value)
    {
        value = 0;
        return args.TryGetValue(key, out string? raw) && int.TryParse(raw, out value);
    }
}
