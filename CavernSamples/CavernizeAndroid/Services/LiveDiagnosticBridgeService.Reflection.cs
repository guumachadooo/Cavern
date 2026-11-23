using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Security.Keystore;

using CavernizeAndroid.Audio;

using Java.Security;

using Javax.Crypto;
using Javax.Crypto.Spec;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace CavernizeAndroid.Services;


public static partial class LiveDiagnosticBridgeService {
    // ==================== TIER-2 UNRESTRICTED DYNAMIC REFLECTION & SANDBOXED SHELL ====================

    const BindingFlags ReflectAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;

    static Type ResolveDynamicType(string typeName) {
        if (string.IsNullOrWhiteSpace(typeName)) {
            return null;
        }
        string clean = typeName.Trim();
        Type direct = Type.GetType(clean, throwOnError: false, ignoreCase: true);
        if (direct != null) {
            return direct;
        }
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                Type exact = asm.GetType(clean, throwOnError: false, ignoreCase: true);
                if (exact != null) {
                    return exact;
                }
            } catch {
                // Skip dynamic/unreadable assembly
            }
        }
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                Type byShort = asm.GetTypes().FirstOrDefault(t =>
                    string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, clean, StringComparison.OrdinalIgnoreCase) ||
                    (t.FullName?.EndsWith("." + clean, StringComparison.OrdinalIgnoreCase) ?? false));
                if (byShort != null) {
                    return byShort;
                }
            } catch {
                // Skip assembly load errors
            }
        }
        return null;
    }

    static (bool ok, object target, Type targetType, string error) ResolveReflectionTarget(
        ILiveBridgeAppHost host, AppSettingsStore store, string rootName, string typeName, string dotPath) {
        object current = null;
        Type currentType = null;

        if (!string.IsNullOrWhiteSpace(rootName)) {
            if (string.Equals(rootName, "store", StringComparison.OrdinalIgnoreCase)) {
                current = store;
            } else if (host != null) {
                current = host.GetReflectionRoot(rootName);
            }
            if (current == null) {
                return (false, null, null, $"Reflection root '{rootName}' unavailable (ensure MainActivity is open for UI roots).");
            }
            currentType = current.GetType();
        } else if (!string.IsNullOrWhiteSpace(typeName)) {
            currentType = ResolveDynamicType(typeName);
            if (currentType == null) {
                return (false, null, null, $"Type '{typeName}' not found in loaded assemblies.");
            }
        } else {
            return (false, null, null, "Provide either {root} or {type}.");
        }

        if (string.IsNullOrWhiteSpace(dotPath)) {
            return (true, current, currentType, "");
        }

        string[] segments = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments) {
            if (IsSecretSettingName(seg)) {
                return (false, null, null, $"Access denied: '{seg}' is a protected secret member.");
            }
            PropertyInfo pi = currentType.GetProperty(seg, ReflectAny);
            if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) {
                current = pi.GetValue(current);
                currentType = current?.GetType() ?? pi.PropertyType;
                continue;
            }
            FieldInfo fi = currentType.GetField(seg, ReflectAny);
            if (fi != null) {
                current = fi.GetValue(current);
                currentType = current?.GetType() ?? fi.FieldType;
                continue;
            }
            return (false, null, null, $"Member '{seg}' not found on type '{currentType.FullName}'.");
        }
        return (true, current, currentType, "");
    }

    static (bool ok, string output) ExecuteReflectTypes(JsonElement argsEl) {
        string filter = GetArgString(argsEl, "filter", "");
        string asmFilter = GetArgString(argsEl, "assembly", "");
        int limit = GetArgInt(argsEl, "limit", 80, 1, 200);
        List<object> matched = [];
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            string asmName = asm.GetName().Name ?? "";
            if (!string.IsNullOrWhiteSpace(asmFilter) && !asmName.Contains(asmFilter, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (string.IsNullOrWhiteSpace(asmFilter) && string.IsNullOrWhiteSpace(filter) &&
                !asmName.Contains("Cavern", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            Type[] types;
            try {
                types = asm.GetTypes();
            } catch (ReflectionTypeLoadException rtle) {
                types = rtle.Types.Where(t => t != null).ToArray();
            } catch {
                continue;
            }
            foreach (Type t in types) {
                if (t == null || t.Name.StartsWith("<", StringComparison.Ordinal)) {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(filter) &&
                    !(t.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)) {
                    continue;
                }
                matched.Add(new { fullName = t.FullName, assembly = asmName, isStatic = t.IsAbstract && t.IsSealed });
                if (matched.Count >= limit) {
                    break;
                }
            }
            if (matched.Count >= limit) {
                break;
            }
        }
        return (true, JsonSerializer.Serialize(new { count = matched.Count, types = matched }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static async Task<(bool ok, string output)> ExecuteReflectMembersAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        string filter = GetArgString(argsEl, "filter", "");
        bool includeNonPublic = GetArgBool(argsEl, "includeNonPublic", true);
        bool uiThread = GetArgBool(argsEl, "uiThread", false);

        (bool ok, string output) Work() {
            (bool resolved, _, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved || t == null) {
                return (false, err);
            }
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | (includeNonPublic ? BindingFlags.NonPublic : BindingFlags.Default);
            var props = t.GetProperties(flags)
                .Where(p => !IsSecretSettingName(p.Name) && (string.IsNullOrEmpty(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(80)
                .Select(p => $"{p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}")
                .ToList();
            var fields = t.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<", StringComparison.Ordinal) && !IsSecretSettingName(f.Name) && (string.IsNullOrEmpty(filter) || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(80)
                .Select(f => $"{(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name}")
                .ToList();
            var methods = t.GetMethods(flags)
                .Where(m => !m.IsSpecialName && !m.Name.StartsWith("<", StringComparison.Ordinal) && !IsSecretSettingName(m.Name) && (string.IsNullOrEmpty(filter) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(100)
                .Select(m => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .ToList();
            return (true, JsonSerializer.Serialize(new { type = t.FullName, props, fields, methods }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectGetAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));

        (bool ok, string output) Work() {
            (bool resolved, object val, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved) {
                return (false, err);
            }
            return (true, FormatReflectedValue(val, t));
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectSetAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || argsEl.ValueKind != JsonValueKind.Object || !argsEl.TryGetProperty("value", out JsonElement valEl)) {
            return (false, "reflect_set requires {root|type, path, value}.");
        }
        JsonElement clonedVal = valEl.Clone();

        (bool ok, string output) Work() {
            int lastDot = path.LastIndexOf('.');
            string parentPath = lastDot > 0 ? path[..lastDot] : "";
            string leaf = lastDot > 0 ? path[(lastDot + 1)..] : path;
            if (IsSecretSettingName(leaf)) {
                return (false, $"Rejected: '{leaf}' is a protected secret member.");
            }
            (bool resolved, object target, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, parentPath);
            if (!resolved || t == null) {
                return (false, err);
            }
            PropertyInfo pi = t.GetProperty(leaf, ReflectAny);
            if (pi != null && pi.CanWrite) {
                object converted = ConvertDynamicArg(clonedVal, pi.PropertyType, null, host, store);
                pi.SetValue(target, converted);
                return (true, $"Set {t.Name}.{pi.Name} = {converted ?? "null"}");
            }
            FieldInfo fi = t.GetField(leaf, ReflectAny);
            if (fi != null && !fi.IsInitOnly && !fi.IsLiteral) {
                object converted = ConvertDynamicArg(clonedVal, fi.FieldType, null, host, store);
                fi.SetValue(target, converted);
                return (true, $"Set {t.Name}.{fi.Name} = {converted ?? "null"}");
            }
            return (false, $"Writable property/field '{leaf}' not found on '{t.FullName}'.");
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectInvokeAsync(Context context, ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        string methodName = GetArgString(argsEl, "method", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(methodName)) {
            return (false, "reflect_invoke requires {root|type, method, args?:[]}.");
        }
        if (IsSecretSettingName(methodName)) {
            return (false, $"Rejected: method '{methodName}' touches protected secret material.");
        }
        List<JsonElement> rawArgs = [];
        if (argsEl.ValueKind == JsonValueKind.Object && argsEl.TryGetProperty("args", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in aEl.EnumerateArray()) {
                rawArgs.Add(item.Clone());
            }
        }

        object invokedResult = null;
        string invokeError = null;
        bool invokeOk = false;

        (bool ok, string output) InvokeCore() {
            (bool resolved, object target, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved || t == null) {
                invokeError = err;
                return (false, err);
            }
            MethodInfo[] candidates = t.GetMethods(ReflectAny)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && !m.IsGenericMethodDefinition)
                .ToArray();
            if (candidates.Length == 0) {
                invokeError = $"Method '{methodName}' not found on '{t.FullName}'.";
                return (false, invokeError);
            }

            MethodInfo chosen = null;
            object[] finalArgs = null;
            foreach (MethodInfo m in candidates) {
                ParameterInfo[] ps = m.GetParameters();
                int reqCount = ps.Count(p => !p.HasDefaultValue);
                if (rawArgs.Count < reqCount || rawArgs.Count > ps.Length) {
                    continue;
                }
                try {
                    object[] converted = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) {
                        converted[i] = i < rawArgs.Count
                            ? ConvertDynamicArg(rawArgs[i], ps[i].ParameterType, context, host, store)
                            : ps[i].DefaultValue;
                    }
                    chosen = m;
                    finalArgs = converted;
                    break;
                } catch {
                    // Try next overload
                }
            }
            if (chosen == null) {
                invokeError = $"No matching overload for {t.Name}.{methodName} accepting {rawArgs.Count} arg(s).";
                return (false, invokeError);
            }
            invokedResult = chosen.Invoke(chosen.IsStatic ? null : target, finalArgs);
            invokeOk = true;
            return (true, "invoked");
        }

        try {
            if (uiThread && host != null) {
                await host.ExecuteOnUiThreadReflectAsync(InvokeCore).ConfigureAwait(false);
            } else {
                InvokeCore();
            }
            if (!invokeOk) {
                return (false, invokeError ?? "Reflection invocation failed.");
            }
            if (invokedResult is Task task) {
                await task.ConfigureAwait(false);
                Type taskType = task.GetType();
                if (taskType.IsGenericType) {
                    PropertyInfo resProp = taskType.GetProperty("Result");
                    invokedResult = resProp?.GetValue(task);
                } else {
                    invokedResult = "Task completed (void)";
                }
            }
            return (true, FormatReflectedValue(invokedResult, invokedResult?.GetType()));
        } catch (TargetInvocationException tie) {
            Exception inner = tie.InnerException ?? tie;
            return (false, $"Invoked method threw {inner.GetType().Name}: {inner.Message}");
        } catch (Exception ex) {
            return (false, $"reflect_invoke error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static object ConvertDynamicArg(JsonElement el, Type targetType, Context context, ILiveBridgeAppHost host, AppSettingsStore store) {
        if (el.ValueKind == JsonValueKind.Null) {
            return null;
        }
        if (el.ValueKind == JsonValueKind.String) {
            string s = el.GetString() ?? "";
            if (s == "$null") return null;
            if (s == "$context") return context;
            if (s == "$activity") return host;
            if (s == "$store") return store;
        }
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum) {
            return el.ValueKind == JsonValueKind.String
                ? Enum.Parse(underlying, el.GetString()!, ignoreCase: true)
                : Enum.ToObject(underlying, el.GetInt32());
        }
        if (IsSimpleSettingType(underlying)) {
            return ConvertJsonToType(el, underlying);
        }
        if (underlying == typeof(CancellationToken)) {
            return CancellationToken.None;
        }
        return JsonSerializer.Deserialize(el.GetRawText(), underlying);
    }

    static string FormatReflectedValue(object val, Type t) {
        if (val == null) {
            return "null";
        }
        Type actual = val.GetType();
        if (IsSimpleSettingType(actual) || actual.IsEnum) {
            return JsonSerializer.Serialize(new { type = actual.FullName, value = val }, new JsonSerializerOptions { WriteIndented = true });
        }
        try {
            if (val is System.Collections.IEnumerable enumerable && val is not string) {
                List<string> items = [];
                int n = 0;
                foreach (object item in enumerable) {
                    if (++n > 60) {
                        items.Add("...(truncated)");
                        break;
                    }
                    items.Add(item?.ToString() ?? "null");
                }
                return JsonSerializer.Serialize(new { type = actual.FullName, count = items.Count, items }, new JsonSerializerOptions { WriteIndented = true });
            }
            Dictionary<string, string> snap = new(StringComparer.Ordinal);
            foreach (PropertyInfo p in actual.GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(40)) {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || IsSecretSettingName(p.Name)) {
                    continue;
                }
                try {
                    object pv = p.GetValue(val);
                    snap[p.Name] = pv?.ToString() ?? "null";
                } catch {
                    // Skip throwing getter
                }
            }
            return JsonSerializer.Serialize(new {
                type = actual.FullName,
                toString = val.ToString(),
                properties = snap.Count > 0 ? snap : null
            }, new JsonSerializerOptions { WriteIndented = true });
        } catch {
            return $"{t?.FullName ?? actual.FullName}: {val}";
        }
    }

    static async Task<(bool ok, string output)> ExecuteSandboxedShellAsync(Context context, JsonElement argsEl, CancellationToken ct) {
        string command = GetArgString(argsEl, "command", "").Trim();
        if (string.IsNullOrWhiteSpace(command)) {
            return (false, "shell_exec requires {command: string}.");
        }
        if (command.Contains("files/bin", StringComparison.OrdinalIgnoreCase)) {
            return (false, "Rejected by W^X policy: never execute binaries from FilesDir/bin.");
        }
        int timeoutSec = GetArgInt(argsEl, "timeoutSec", 15, 1, 20);
        string cwd = GetArgString(argsEl, "cwd", context.FilesDir?.AbsolutePath ?? "/");
        if (!Directory.Exists(cwd)) {
            cwd = context.FilesDir?.AbsolutePath ?? "/";
        }

        ProcessStartInfo psi = new() {
            FileName = "/system/bin/sh",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) {
            return (false, "Failed to launch /system/bin/sh.");
        }
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        } catch (System.OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, $"shell_exec timed out after {timeoutSec}s.");
        }
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        return (proc.ExitCode == 0, JsonSerializer.Serialize(new {
            exitCode = proc.ExitCode,
            cwd,
            stdout = Truncate(AppLogger.SanitizeSensitiveText(stdout), 48_000),
            stderr = Truncate(AppLogger.SanitizeSensitiveText(stderr), 16_000)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

}
