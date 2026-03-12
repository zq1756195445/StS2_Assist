using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HarmonyLib;

namespace Sts2EventHook;

internal static class Bootstrap
{
    private const string DefaultBridgeAddress = "127.0.0.1:43125";
    private static readonly object InitLock = new();
    private static readonly object SendLock = new();
    private static readonly object LogLock = new();
    private static bool _initialized;
    private static readonly Harmony Harmony = new("sts2.assist.event-hook");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, byte> PatchedMethods = new(StringComparer.Ordinal);

    internal static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                TryPatchAll($"assembly-load:{args.LoadedAssembly.GetName().Name}");
            };

            TryPatchAll("init");
            _initialized = true;
            Log("initialized");
            SendRefresh("event(hook:init)", null);
        }
    }

    private static void TryPatchAll(string reason)
    {
        Log($"patch scan: {reason}");
        foreach (HookCandidate candidate in CombatHookManifest.All)
        {
            PatchCandidate(candidate);
        }

        foreach (HookCandidate candidate in SceneHookManifest.All)
        {
            PatchCandidate(candidate);
        }
    }

    private static void PatchCandidate(HookCandidate candidate)
    {
        Type? type = AccessTools.TypeByName(candidate.TypeName);
        if (type is null)
        {
            Log($"type missing: {candidate.TypeName}");
            return;
        }

        IEnumerable<MethodBase> methods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => candidate.Matches(method.Name))
            .Cast<MethodBase>();

        foreach (MethodBase method in methods)
        {
            string patchKey = $"{method.DeclaringType?.FullName ?? "unknown"}::{method}";
            if (!PatchedMethods.TryAdd(patchKey, 0))
            {
                continue;
            }

            try
            {
                Harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(Bootstrap), nameof(OnTriggered)));
                Log($"patched: {patchKey}");
            }
            catch (Exception ex)
            {
                Log($"patch failed: {patchKey} :: {ex.GetType().Name} :: {ex.Message}");
            }
        }
    }

    private static void OnTriggered(MethodBase __originalMethod)
    {
        string typeName = __originalMethod.DeclaringType?.FullName ?? "unknown";
        string methodName = __originalMethod.Name;
        Log($"triggered: {typeName}.{methodName}");
        SendRefresh(null, new HookTrigger(typeName, methodName));
    }

    private static void SendRefresh(string? source, HookTrigger? trigger)
    {
        string address = Environment.GetEnvironmentVariable("STS2_HUD_EVENT_BRIDGE_ADDR")
            ?? DefaultBridgeAddress;
        string[] parts = address.Split(':', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            Log($"invalid bridge address: {address}");
            return;
        }

        var payload = new HookEvent("refresh", source, trigger);
        string line = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
        byte[] buffer = Encoding.UTF8.GetBytes(line);

        lock (SendLock)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(parts[0], port);
                using NetworkStream stream = client.GetStream();
                stream.Write(buffer, 0, buffer.Length);
                stream.Flush();
                Log($"event sent: {payload.Kind} {trigger?.TypeName}.{trigger?.MethodName}");
            }
            catch (Exception ex)
            {
                Log($"event send failed: {ex.GetType().Name} :: {ex.Message}");
            }
        }
    }

    private static void Log(string message)
    {
        string path = Environment.GetEnvironmentVariable("STS2_HOOK_LOG")
            ?? Path.Combine(Path.GetTempPath(), "sts2_event_hook.log");
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";

        lock (LogLock)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch
            {
            }
        }
    }
}

internal sealed record HookEvent(string Kind, string? Source, HookTrigger? Trigger);

internal sealed record HookTrigger(string TypeName, string MethodName);

internal sealed record HookCandidate(string TypeName, string[] MethodNames, bool PartialMatch = false)
{
    internal bool Matches(string methodName)
    {
        return PartialMatch
            ? MethodNames.Any(methodName.Contains)
            : MethodNames.Any(name => string.Equals(name, methodName, StringComparison.Ordinal));
    }
}

