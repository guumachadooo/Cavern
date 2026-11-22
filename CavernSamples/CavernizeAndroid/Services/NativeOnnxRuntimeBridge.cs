using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Parses the extracted <c>onnxruntime_c_api.h</c> header to build an exact 0-based vtable slot map for
/// <c>struct OrtApi</c> across any ONNX Runtime version (1.17–1.31+) without hardcoded fragile offsets.
/// Evaluates Android ARM64 C preprocessor conditionals (<c>#ifdef _WIN32</c>, <c>#if</c>, <c>#else</c>, <c>#endif</c>),
/// handles multi-line macro declarations with nested parentheses, and validates canonical anchor slots.
/// </summary>
public static class OnnxRuntimeHeaderParser {
    static readonly Regex ApiVersionRegex = new(@"#\s*define\s+ORT_API_VERSION\s+(\d+)", RegexOptions.Compiled);
    static readonly Regex StructHeaderRegex = new(@"\bstruct\s+OrtApi\s*\{", RegexOptions.Compiled);
    static readonly Regex MacroStartRegex = new(@"\b(ORT_CLASS_RELEASE|ORT_API2_STATUS|ORT_API_STATUS|ORT_API_T|ORT_API)\s*\(", RegexOptions.Compiled);
    static readonly Regex RawFnPtrRegex = new(@"\(\s*(?:[A-Za-z0-9_]+\s*)*\*\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\(", RegexOptions.Compiled);

    static readonly HashSet<string> AndroidDefinedMacros = new(StringComparer.Ordinal) {
        "__ANDROID__",
        "ANDROID",
        "__linux__",
        "__unix__",
        "__aarch64__",
        "__LP64__"
    };

    /// <summary>
    /// Extracts the advertised <c>ORT_API_VERSION</c> and a validated map from C API function name
    /// (e.g. <c>CreateSession</c>, <c>ReleaseSession</c>) to its 0-based pointer slot index inside <c>struct OrtApi</c>.
    /// </summary>
    public static (uint ApiVersion, Dictionary<string, int> SlotMap) ParseOrtApiHeader(string headerText) {
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(headerText)) {
            return (17u, slots);
        }

        string noComments = StripCommentsPreservingNewlines(headerText);
        string preprocessed = PreprocessForAndroidArm64(noComments);

        uint apiVersion = 17u;
        Match verMatch = ApiVersionRegex.Match(preprocessed);
        if (verMatch.Success && uint.TryParse(verMatch.Groups[1].Value, out uint parsed)) {
            apiVersion = parsed;
        }

        Match structMatch = StructHeaderRegex.Match(preprocessed);
        if (!structMatch.Success) {
            return (apiVersion, slots);
        }

        int openBrace = preprocessed.IndexOf('{', structMatch.Index);
        int closeBrace = FindMatchingDelimiter(preprocessed, openBrace, '{', '}');
        if (openBrace < 0 || closeBrace <= openBrace) {
            return (apiVersion, slots);
        }

        string body = preprocessed.Substring(openBrace + 1, closeBrace - openBrace - 1);
        List<string> statements = SplitTopLevelStatements(body);

        int slotIndex = 0;
        for (int i = 0; i < statements.Count; i++) {
            string stmt = statements[i];
            if (string.IsNullOrWhiteSpace(stmt) || stmt.StartsWith('#')) {
                continue;
            }
            string symbol = TryExtractMemberSymbol(stmt);
            if (!string.IsNullOrEmpty(symbol)) {
                slots[symbol] = slotIndex++;
            }
        }

        // Validate canonical immutable OrtApi anchor slots (v1..v40+ ABI guarantee)
        if (!ValidateAnchorSlots(slots, out _)) {
            slots.Clear();
        }

        return (apiVersion, slots);
    }

    /// <summary>
    /// Diagnostic variant of <see cref="ParseOrtApiHeader"/> returning the failure reason when anchor validation fails.
    /// </summary>
    public static (uint ApiVersion, Dictionary<string, int> SlotMap, string Diagnostic) ParseOrtApiHeaderWithDiagnostics(string headerText) {
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(headerText)) {
            return (17u, slots, "header asset is empty or missing");
        }

        string noComments = StripCommentsPreservingNewlines(headerText);
        string preprocessed = PreprocessForAndroidArm64(noComments);

        uint apiVersion = 17u;
        Match verMatch = ApiVersionRegex.Match(preprocessed);
        if (verMatch.Success && uint.TryParse(verMatch.Groups[1].Value, out uint parsed)) {
            apiVersion = parsed;
        }

        Match structMatch = StructHeaderRegex.Match(preprocessed);
        if (!structMatch.Success) {
            return (apiVersion, slots, $"struct OrtApi not found (headerLen={headerText.Length})");
        }

        int openBrace = preprocessed.IndexOf('{', structMatch.Index);
        int closeBrace = FindMatchingDelimiter(preprocessed, openBrace, '{', '}');
        if (openBrace < 0 || closeBrace <= openBrace) {
            return (apiVersion, slots, $"struct OrtApi braces unbalanced (headerLen={headerText.Length})");
        }

        string body = preprocessed.Substring(openBrace + 1, closeBrace - openBrace - 1);
        List<string> statements = SplitTopLevelStatements(body);

        int slotIndex = 0;
        for (int i = 0; i < statements.Count; i++) {
            string stmt = statements[i];
            if (string.IsNullOrWhiteSpace(stmt) || stmt.StartsWith('#')) {
                continue;
            }
            string symbol = TryExtractMemberSymbol(stmt);
            if (!string.IsNullOrEmpty(symbol)) {
                slots[symbol] = slotIndex++;
            }
        }

        if (!ValidateAnchorSlots(slots, out string reason)) {
            int rawCount = slots.Count;
            slots.Clear();
            return (apiVersion, slots, $"anchor validation failed ({reason}, parsedSlots={rawCount}, stmts={statements.Count}, apiVer={apiVersion})");
        }

        return (apiVersion, slots, string.Empty);
    }

    static bool ValidateAnchorSlots(Dictionary<string, int> slots, out string reason) {
        if (slots.Count < 20) {
            reason = $"too few slots ({slots.Count})";
            return false;
        }
        if (!slots.TryGetValue("CreateStatus", out int s0) || s0 != 0) {
            reason = $"CreateStatus={(!slots.ContainsKey("CreateStatus") ? "missing" : s0.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("GetErrorCode", out int s1) || s1 != 1) {
            reason = $"GetErrorCode={(!slots.ContainsKey("GetErrorCode") ? "missing" : s1.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("GetErrorMessage", out int s2) || s2 != 2) {
            reason = $"GetErrorMessage={(!slots.ContainsKey("GetErrorMessage") ? "missing" : s2.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("CreateEnv", out int s3) || s3 != 3) {
            reason = $"CreateEnv={(!slots.ContainsKey("CreateEnv") ? "missing" : s3.ToString())}";
            return false;
        }
        if (!slots.ContainsKey("CreateSession") || !slots.ContainsKey("Run") ||
            !slots.ContainsKey("ReleaseSession") || !slots.ContainsKey("ReleaseStatus")) {
            reason = "missing core CreateSession/Run/Release slots";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    static string TryExtractMemberSymbol(string stmt) {
        Match m = MacroStartRegex.Match(stmt);
        if (m.Success) {
            string macro = m.Groups[1].Value;
            int openParen = stmt.IndexOf('(', m.Index);
            int closeParen = FindMatchingDelimiter(stmt, openParen, '(', ')');
            if (openParen >= 0 && closeParen > openParen) {
                string argsText = stmt.Substring(openParen + 1, closeParen - openParen - 1);
                List<string> args = SplitTopLevelCommas(argsText);
                if (macro == "ORT_CLASS_RELEASE") {
                    string cls = args.Count >= 1 ? CleanIdentifier(args[0]) : "";
                    return string.IsNullOrEmpty(cls) ? null : "Release" + cls;
                }
                if (macro == "ORT_API2_STATUS" || macro == "ORT_API_STATUS") {
                    // ORT_API2_STATUS(Name, ...) -> Name is argument 0
                    return args.Count >= 1 ? CleanIdentifier(args[0]) : null;
                }
                // ORT_API(ReturnType, Name, ...) / ORT_API_T(ReturnType, Name, ...) -> Name is argument 1
                return args.Count >= 2 ? CleanIdentifier(args[1]) : null;
            }
        }

        Match raw = RawFnPtrRegex.Match(stmt);
        if (raw.Success) {
            return CleanIdentifier(raw.Groups[1].Value);
        }
        return null;
    }

    static List<string> SplitTopLevelStatements(string body) {
        List<string> results = [];
        StringBuilder current = new(128);
        int parenDepth = 0;
        int braceDepth = 0;

        for (int i = 0; i < body.Length; i++) {
            char c = body[i];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == '{') braceDepth++;
            else if (c == '}' && braceDepth > 0) braceDepth--;

            if (c == ';' && parenDepth == 0 && braceDepth == 0) {
                string s = current.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) {
                    results.Add(s);
                }
                current.Clear();
            } else {
                current.Append(c);
            }
        }
        string tail = current.ToString().Trim();
        if (!string.IsNullOrEmpty(tail)) {
            results.Add(tail);
        }
        return results;
    }

    static List<string> SplitTopLevelCommas(string text) {
        List<string> parts = [];
        int parenDepth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == ',' && parenDepth == 0) {
                parts.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(text.Substring(start).Trim());
        return parts;
    }

    static int FindMatchingDelimiter(string text, int openIndex, char openChar, char closeChar) {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != openChar) {
            return -1;
        }
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++) {
            char c = text[i];
            if (c == openChar) {
                depth++;
            } else if (c == closeChar) {
                depth--;
                if (depth == 0) {
                    return i;
                }
            }
        }
        return -1;
    }

    static string CleanIdentifier(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            return null;
        }
        string t = token.Trim();
        // Extract trailing valid C identifier if decorated (e.g. "ORT_API_CALL CreateStatus")
        int end = t.Length - 1;
        while (end >= 0 && !IsIdentChar(t[end])) end--;
        if (end < 0) return null;
        int start = end;
        while (start > 0 && IsIdentChar(t[start - 1])) start--;
        return t.Substring(start, end - start + 1);
    }

    static bool IsIdentChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    static string StripCommentsPreservingNewlines(string text) {
        StringBuilder sb = new(text.Length);
        int i = 0;
        int n = text.Length;
        while (i < n) {
            if (i + 1 < n && text[i] == '/' && text[i + 1] == '*') {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }
                i = Math.Min(n, i + 2);
                sb.Append(' ');
            } else if (i + 1 < n && text[i] == '/' && text[i + 1] == '/') {
                i += 2;
                while (i < n && text[i] != '\n') i++;
            } else {
                sb.Append(text[i++]);
            }
        }
        return sb.ToString();
    }

    struct PreprocFrame {
        public bool ParentActive;
        public bool BranchActive;
        public bool BranchTaken;
    }

    static string PreprocessForAndroidArm64(string text) {
        // Join backslash-escaped line continuations first
        string joined = text.Replace("\\\r\n", " ").Replace("\\\n", " ");
        StringBuilder output = new(joined.Length);
        Stack<PreprocFrame> stack = new();
        bool active = true;

        using StringReader sr = new(joined);
        string line;
        while ((line = sr.ReadLine()) != null) {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) {
                string directive = trimmed[1..].TrimStart();
                if (directive.StartsWith("ifdef ", StringComparison.Ordinal) || directive.StartsWith("ifdef\t", StringComparison.Ordinal)) {
                    string sym = ExtractFirstToken(directive[5..]);
                    bool cond = AndroidDefinedMacros.Contains(sym);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("ifndef ", StringComparison.Ordinal) || directive.StartsWith("ifndef\t", StringComparison.Ordinal)) {
                    string sym = ExtractFirstToken(directive[6..]);
                    bool cond = !AndroidDefinedMacros.Contains(sym);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("if ", StringComparison.Ordinal) || directive.StartsWith("if\t", StringComparison.Ordinal) || directive.StartsWith("if(", StringComparison.Ordinal)) {
                    string expr = directive[2..].Trim();
                    bool cond = EvaluatePreprocCondition(expr);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("elif ", StringComparison.Ordinal) || directive.StartsWith("elif\t", StringComparison.Ordinal) || directive.StartsWith("elif(", StringComparison.Ordinal)) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        bool cond = !top.BranchTaken && EvaluatePreprocCondition(directive[4..].Trim());
                        bool nextActive = top.ParentActive && cond;
                        top.BranchActive = nextActive;
                        top.BranchTaken = top.BranchTaken || cond;
                        stack.Push(top);
                        active = nextActive;
                    }
                    continue;
                }
                if (directive.StartsWith("else", StringComparison.Ordinal) &&
                    (directive.Length == 4 || !IsIdentChar(directive[4]))) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        bool nextActive = top.ParentActive && !top.BranchTaken;
                        top.BranchActive = nextActive;
                        top.BranchTaken = true;
                        stack.Push(top);
                        active = nextActive;
                    }
                    continue;
                }
                if (directive.StartsWith("endif", StringComparison.Ordinal) &&
                    (directive.Length == 5 || !IsIdentChar(directive[5]))) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        active = top.ParentActive;
                    }
                    continue;
                }
                // Preserve #define ORT_API_VERSION lines when active; drop other directives
                if (active && directive.StartsWith("define", StringComparison.Ordinal) && directive.Contains("ORT_API_VERSION", StringComparison.Ordinal)) {
                    output.AppendLine(trimmed);
                }
                continue;
            }

            if (active) {
                output.AppendLine(line);
            }
        }
        return output.ToString();
    }

    static string ExtractFirstToken(string s) {
        s = s.TrimStart();
        int i = 0;
        while (i < s.Length && IsIdentChar(s[i])) i++;
        return s.Substring(0, i);
    }

    static bool EvaluatePreprocCondition(string expr) {
        if (string.IsNullOrWhiteSpace(expr)) {
            return false;
        }
        // Replace defined(SYM) and defined SYM with 1 / 0
        string replaced = Regex.Replace(expr, @"\bdefined\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");
        replaced = Regex.Replace(replaced, @"\bdefined\s+([A-Za-z_][A-Za-z0-9_]*)\b",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");
        // Replace remaining bare identifiers with 1 if defined on Android ARM64, else 0
        replaced = Regex.Replace(replaced, @"\b([A-Za-z_][A-Za-z0-9_]*)\b",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");

        int pos = 0;
        return ParseOr(replaced, ref pos) != 0;
    }

    static long ParseOr(string s, ref int pos) {
        long left = ParseAnd(s, ref pos);
        while (MatchToken(s, ref pos, "||")) {
            long right = ParseAnd(s, ref pos);
            left = (left != 0 || right != 0) ? 1 : 0;
        }
        return left;
    }

    static long ParseAnd(string s, ref int pos) {
        long left = ParseComparison(s, ref pos);
        while (MatchToken(s, ref pos, "&&")) {
            long right = ParseComparison(s, ref pos);
            left = (left != 0 && right != 0) ? 1 : 0;
        }
        return left;
    }

    static long ParseComparison(string s, ref int pos) {
        long left = ParseUnary(s, ref pos);
        if (MatchToken(s, ref pos, "==")) return left == ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "!=")) return left != ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, ">=")) return left >= ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "<=")) return left <= ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, ">")) return left > ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "<")) return left < ParseUnary(s, ref pos) ? 1 : 0;
        return left;
    }

    static long ParseUnary(string s, ref int pos) {
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == '!') {
            pos++;
            return ParseUnary(s, ref pos) == 0 ? 1 : 0;
        }
        if (pos < s.Length && s[pos] == '(') {
            pos++;
            long val = ParseOr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ')') pos++;
            return val;
        }
        int start = pos;
        if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
        while (pos < s.Length && char.IsDigit(s[pos])) pos++;
        if (pos > start && long.TryParse(s.Substring(start, pos - start), out long num)) {
            return num;
        }
        return 0;
    }

    static void SkipWs(string s, ref int pos) {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    static bool MatchToken(string s, ref int pos, string tok) {
        SkipWs(s, ref pos);
        if (pos + tok.Length <= s.Length && string.CompareOrdinal(s, pos, tok, 0, tok.Length) == 0) {
            // Avoid matching '>' when '>=' or '!' when '!='
            if (tok.Length == 1 && (tok[0] == '>' || tok[0] == '<') && pos + 1 < s.Length && s[pos + 1] == '=') {
                return false;
            }
            pos += tok.Length;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Dynamic Android native bridge for <c>libonnxruntime.so</c> bundled in <see cref="Android.Content.PM.ApplicationInfo.NativeLibraryDir"/>.
/// Implements <see cref="INeuralInferenceRuntime"/> and <see cref="INeuralModelRegistry"/> using
/// companion <c>.so</c> pre-loading + <see cref="NativeLibrary.Load"/> + <c>OrtGetApiBase()</c> +
/// asset-bundled <c>onnxruntime_c_api.h</c> vtable slots, with automatic QNN HTP -> XNNPACK -> CPU provider cascade.
/// </summary>
public sealed class NativeOnnxRuntimeBridge : INeuralInferenceRuntime, INeuralModelRegistry {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr OrtGetApiBaseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetApiDelegate(uint version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetVersionStringDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateEnvDelegate(int logSeverityLevel, [MarshalAs(UnmanagedType.LPUTF8Str)] string logId, out IntPtr env);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateSessionOptionsDelegate(out IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr options, int intraOpNumThreads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetInterOpNumThreadsDelegate(IntPtr options, int interOpNumThreads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetGraphOptimizationLevelDelegate(IntPtr options, int graphOptimizationLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AddSessionConfigEntryDelegate(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionOptionsAppendEpDelegate(
        IntPtr options,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string providerName,
        IntPtr[] keys,
        IntPtr[] values,
        UIntPtr numKeys);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateSessionDelegate(IntPtr env, [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath, IntPtr options, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetCountDelegate(IntPtr session, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetNameDelegate(IntPtr session, UIntPtr index, IntPtr allocator, out IntPtr namePtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetTypeInfoDelegate(IntPtr session, UIntPtr index, out IntPtr typeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CastTypeInfoToTensorInfoDelegate(IntPtr typeInfo, out IntPtr tensorInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetAllocatorWithDefaultOptionsDelegate(out IntPtr allocator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AllocatorFreeDelegate(IntPtr allocator, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateCpuMemoryInfoDelegate(int allocType, int memType, out IntPtr memInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateTensorWithDataAsOrtValueDelegate(
        IntPtr info,
        IntPtr pData,
        UIntPtr pDataByteLength,
        long[] shape,
        UIntPtr shapeLen,
        int tensorElementDataType,
        out IntPtr ortValue);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr RunDelegate(
        IntPtr session,
        IntPtr runOptions,
        IntPtr[] inputNames,
        IntPtr[] inputs,
        UIntPtr inputLen,
        IntPtr[] outputNames,
        UIntPtr outputNamesLen,
        IntPtr[] outputs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorMutableDataDelegate(IntPtr value, out IntPtr pData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorTypeAndShapeDelegate(IntPtr value, out IntPtr typeAndShapeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorElementTypeDelegate(IntPtr info, out int elementType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetDimensionsCountDelegate(IntPtr info, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetDimensionsDelegate(IntPtr info, long[] dimValues, UIntPtr dimValuesLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetErrorMessageDelegate(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ReleaseHandleDelegate(IntPtr handle);

    const int OrtLoggingLevelWarning = 2;
    const int OrtEnableAll = 99;
    const int OrtArenaAllocator = 0;
    const int OrtMemTypeDefault = 0;
    const int OnnxTensorElementDataTypeFloat = 1;
    const int OnnxTensorElementDataTypeFloat16 = 10;
    const int OnnxTensorElementDataTypeDouble = 11;
    const long MaxTensorElements = 64L * 1024L * 1024L; // 256 MB float32 ceiling to prevent mobile OOM / 32-bit length overflow
    const float Float16FiniteMax = 65504f;
    const double ModelMemoryExpansionFactor = 2.5; // Protobuf parse + graph opt + EP weight packing peak multiplier
    const long SessionBaseOverheadBytes = 32L * 1024L * 1024L; // 32 MB base ORT runtime & activation arena overhead
    const long MinimumSafetyHeadroomBytes = 192L * 1024L * 1024L; // 192 MB OS/audio engine LMK safety headroom

    static readonly string[] PreferredCompanionLoadOrder = [
        "libonnxruntime_providers_shared.so",
        "libQnnSystem.so",
        "libQnnHtpPrepare.so",
        "libQnnHtp.so"
    ];

    readonly Context appContext;
    readonly object initLock = new();
    readonly List<IntPtr> companionLibHandles = [];

    IntPtr libHandle;
    IntPtr ortApiPtr;
    IntPtr ortEnv;
    IntPtr cpuMemoryInfo;
    IntPtr defaultAllocator;
    Dictionary<string, int> apiSlots = new(StringComparer.Ordinal);
    string loadedVersion = "";
    string lastActiveProvider = "";
    string lastInitError = "";
    int activeSessionCount;
    bool disposeRequested;
    bool initialized;

    // Cached C API delegates
    CreateEnvDelegate fnCreateEnv;
    CreateSessionOptionsDelegate fnCreateSessionOptions;
    SetIntraOpNumThreadsDelegate fnSetIntraOpNumThreads;
    SetInterOpNumThreadsDelegate fnSetInterOpNumThreads;
    SetGraphOptimizationLevelDelegate fnSetGraphOpt;
    AddSessionConfigEntryDelegate fnAddSessionConfig;
    SessionOptionsAppendEpDelegate fnAppendEp;
    CreateSessionDelegate fnCreateSession;
    SessionGetCountDelegate fnGetInputCount;
    SessionGetCountDelegate fnGetOutputCount;
    SessionGetNameDelegate fnGetInputName;
    SessionGetNameDelegate fnGetOutputName;
    SessionGetTypeInfoDelegate fnGetInputTypeInfo;
    CastTypeInfoToTensorInfoDelegate fnCastTypeInfoToTensorInfo;
    GetAllocatorWithDefaultOptionsDelegate fnGetAllocator;
    AllocatorFreeDelegate fnAllocatorFree;
    CreateCpuMemoryInfoDelegate fnCreateCpuMemInfo;
    CreateTensorWithDataAsOrtValueDelegate fnCreateTensor;
    RunDelegate fnRun;
    GetTensorMutableDataDelegate fnGetTensorData;
    GetTensorTypeAndShapeDelegate fnGetTensorTypeAndShape;
    GetTensorElementTypeDelegate fnGetTensorElementType;
    GetDimensionsCountDelegate fnGetDimsCount;
    GetDimensionsDelegate fnGetDims;
    GetErrorMessageDelegate fnGetErrorMessage;
    ReleaseHandleDelegate fnReleaseStatus;
    ReleaseHandleDelegate fnReleaseEnv;
    ReleaseHandleDelegate fnReleaseSession;
    ReleaseHandleDelegate fnReleaseSessionOptions;
    ReleaseHandleDelegate fnReleaseValue;
    ReleaseHandleDelegate fnReleaseMemoryInfo;
    ReleaseHandleDelegate fnReleaseTensorTypeAndShapeInfo;
    ReleaseHandleDelegate fnReleaseTypeInfo;

    public NativeOnnxRuntimeBridge(Context context) {
        appContext = context?.ApplicationContext ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public bool IsAvailable {
        get {
            lock (initLock) {
                return !disposeRequested && (initialized || OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext));
            }
        }
    }

    /// <inheritdoc/>
    public NeuralRuntimeInfo GetRuntimeInfo() {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext);
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                   OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        bool ready;
        string version;
        string activeEp;

        lock (initLock) {
            if (!disposeRequested && installed && !initialized) {
                TryInitializeLocked();
            }
            ready = !disposeRequested && installed && initialized;
            version = !string.IsNullOrEmpty(loadedVersion) ? loadedVersion : (installed ? "installed" : string.Empty);
            activeEp = !ready
                ? "Not Installed"
                : (!string.IsNullOrEmpty(lastActiveProvider)
                    ? lastActiveProvider
                    : (qnn ? "QNN HTP (NPU)" : "XNNPACK (ARM64 SIMD)"));
        }

        return new NeuralRuntimeInfo {
            IsAvailable = ready,
            LibraryPath = installed ? OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext) : string.Empty,
            VersionString = version,
            ActiveProvider = activeEp,
            IsQnnAvailable = qnn,
            DiagnosticMessage = ready
                ? string.Empty
                : (installed
                    ? (!string.IsNullOrEmpty(lastInitError) ? lastInitError : "Native ONNX Runtime failed to initialize.")
                    : "ONNX Runtime is not installed.")
        };
    }

    /// <inheritdoc/>
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() =>
        OnnxRuntimeInstallerService.GetAllRegisteredModels(appContext);

    /// <inheritdoc/>
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() =>
        OnnxRuntimeInstallerService.GetInstalledModelEntries(appContext);

    /// <inheritdoc/>
    public string ResolveInstalledModelPath(string modelId) {
        string path = OnnxRuntimeInstallerService.ResolveModelPath(appContext, modelId);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// Evaluates current Android system RAM against the estimated peak memory required to load
    /// <paramref name="modelPath"/> (including any companion ONNX external-data tensor files) into ONNX Runtime.
    /// </summary>
    public ModelMemoryCheckResult CheckMemoryForModel(string modelPath) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            return new ModelMemoryCheckResult {
                HasSufficientMemory = false,
                DiagnosticMessage = "ONNX model file not found."
            };
        }
        long primaryBytes = new FileInfo(modelPath).Length;
        long totalModelBytes = ResolveTotalModelSizeBytes(modelPath, primaryBytes);
        return CheckMemoryForModelBytes(totalModelBytes, Path.GetFileName(modelPath));
    }

    /// <summary>
    /// Evaluates current Android system RAM against the estimated peak native memory required for a model
    /// of <paramref name="modelBytes"/> bytes without touching disk or initializing a session.
    /// </summary>
    public ModelMemoryCheckResult CheckMemoryForModelBytes(long modelBytes, string modelLabel = "") {
        long safeModelBytes = Math.Max(0L, modelBytes);
        long estimatedModelBytes = EstimateRequiredModelBytes(safeModelBytes);

        if (!TryReadSystemMemory(out long availBytes, out long totalBytes, out long thresholdBytes, out bool isLowMemory, out string source)) {
            return new ModelMemoryCheckResult {
                HasSufficientMemory = true,
                IsLowMemoryState = false,
                ModelFileBytes = safeModelBytes,
                EstimatedRequiredBytes = estimatedModelBytes,
                AvailableSystemBytes = 0L,
                TotalSystemBytes = 0L,
                LowMemoryThresholdBytes = 0L,
                SafetyHeadroomBytes = MinimumSafetyHeadroomBytes,
                Source = "Unavailable",
                DiagnosticMessage = $"System RAM telemetry unavailable; proceeding with model '{modelLabel}' ({FormatMiB(safeModelBytes)} MB, est. {FormatMiB(estimatedModelBytes)} MB)."
            };
        }

        long headroomBytes = ComputeEffectiveSafetyHeadroom(totalBytes, thresholdBytes);
        long requiredBudgetBytes = estimatedModelBytes > long.MaxValue - headroomBytes
            ? long.MaxValue
            : estimatedModelBytes + headroomBytes;

        bool sufficient = !isLowMemory && availBytes >= requiredBudgetBytes;
        string namePart = string.IsNullOrWhiteSpace(modelLabel) ? "ONNX model" : $"'{modelLabel}'";
        string status = sufficient ? "OK" : (isLowMemory ? "LOW_MEMORY" : "INSUFFICIENT_RAM");
        string diag = $"{status} [{source}] for {namePart}: file={FormatMiB(safeModelBytes)} MB, estLoad={FormatMiB(estimatedModelBytes)} MB, headroom={FormatMiB(headroomBytes)} MB, need={FormatMiB(requiredBudgetBytes)} MB, avail={FormatMiB(availBytes)} MB / total={FormatMiB(totalBytes)} MB (lowMem={isLowMemory})";

        return new ModelMemoryCheckResult {
            HasSufficientMemory = sufficient,
            IsLowMemoryState = isLowMemory,
            ModelFileBytes = safeModelBytes,
            EstimatedRequiredBytes = requiredBudgetBytes,
            AvailableSystemBytes = availBytes,
            TotalSystemBytes = totalBytes,
            LowMemoryThresholdBytes = thresholdBytes,
            SafetyHeadroomBytes = headroomBytes,
            Source = source,
            DiagnosticMessage = diag
        };
    }

    /// <inheritdoc/>
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 4) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            throw new FileNotFoundException("ONNX model file not found.", modelPath);
        }
        long modelBytes = new FileInfo(modelPath).Length;
        if (modelBytes < 64) {
            throw new InvalidDataException($"ONNX model file '{Path.GetFileName(modelPath)}' is empty or truncated ({modelBytes} bytes).");
        }

        EnsureSufficientMemoryForModel(modelPath, modelBytes);

        lock (initLock) {
            if (disposeRequested) {
                throw new ObjectDisposedException(nameof(NativeOnnxRuntimeBridge));
            }
            if (!initialized && !TryInitializeLocked()) {
                string detail = !string.IsNullOrWhiteSpace(lastInitError)
                    ? lastInitError
                    : "Native library or C API header initialization failed.";
                throw new InvalidOperationException($"ONNX Runtime could not be initialized ({detail}).");
            }
            // Pin native runtime lifetime (ortEnv, cpuMemoryInfo, libHandle) while session creation & inference run
            activeSessionCount++;
        }

        bool handoffComplete = false;
        try {
            int threads = intraOpThreads > 0 ? intraOpThreads : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            List<NeuralExecutionProvider> candidates = BuildSessionProviderCascade(preferredProvider);
            Exception lastErr = null;

            for (int c = 0; c < candidates.Count; c++) {
                NeuralExecutionProvider candidate = candidates[c];
                IntPtr sessOpts = IntPtr.Zero;
                CheckStatus(fnCreateSessionOptions(out sessOpts));
                try {
                    CheckStatus(fnSetIntraOpNumThreads(sessOpts, threads));
                    if (fnSetInterOpNumThreads != null) {
                        ReleaseStatusQuietly(fnSetInterOpNumThreads(sessOpts, 1));
                    }
                    CheckStatus(fnSetGraphOpt(sessOpts, OrtEnableAll));
                    if (fnAddSessionConfig != null) {
                        ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.intra_op.allow_spinning", "0"));
                        ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.inter_op.allow_spinning", "0"));
                        ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.set_denormal_as_zero", "1"));
                        ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "memory.enable_memory_arena_shrinkage", "cpu:0"));
                    }

                    string activeEp = ConfigureSingleExecutionProvider(sessOpts, candidate, threads);
                    if (candidate != NeuralExecutionProvider.Cpu && activeEp == "CPU") {
                        // Requested accelerator EP could not be appended; skip duplicate CPU run if CPU is already queued next
                        if (c + 1 < candidates.Count) {
                            continue;
                        }
                    }

                    IntPtr st = fnCreateSession(ortEnv, modelPath, sessOpts, out IntPtr sessionHandle);
                    if (st == IntPtr.Zero && sessionHandle != IntPtr.Zero) {
                        lock (initLock) {
                            lastActiveProvider = activeEp;
                        }
                        NativeOnnxSession session = new(this, modelPath, sessionHandle, activeEp);
                        handoffComplete = true;
                        return session;
                    }

                    string errMsg = ReadAndReleaseStatus(st);
                    lastErr = new InvalidOperationException($"ONNX Runtime error ({activeEp}): {errMsg}");
                    if (c + 1 < candidates.Count) {
                        AppLogger.Warn($"ONNX CreateSession failed on {activeEp} for '{Path.GetFileName(modelPath)}'; retrying next EP: {errMsg}");
                    }
                } finally {
                    if (sessOpts != IntPtr.Zero) {
                        fnReleaseSessionOptions?.Invoke(sessOpts);
                    }
                }
            }

            throw lastErr ?? new InvalidOperationException($"Failed to create ONNX Runtime session for '{Path.GetFileName(modelPath)}'.");
        } finally {
            if (!handoffComplete) {
                ReleaseSessionRef();
            }
        }
    }

    void EnsureSufficientMemoryForModel(string modelPath, long primaryModelBytes) {
        long totalModelBytes = ResolveTotalModelSizeBytes(modelPath, primaryModelBytes);
        string fileName = Path.GetFileName(modelPath);
        ModelMemoryCheckResult check = CheckMemoryForModelBytes(totalModelBytes, fileName);
        if (!check.HasSufficientMemory) {
            // Reclaim any lingering managed audio buffers from prior runs once before rejecting load
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            check = CheckMemoryForModelBytes(totalModelBytes, fileName);
        }

        if (!check.HasSufficientMemory) {
            AppLogger.Warn($"ONNX memory guard blocked loading '{fileName}': {check.DiagnosticMessage}");
            throw new InsufficientMemoryException(
                $"Insufficient system RAM to safely load ONNX model '{fileName}' ({FormatMiB(totalModelBytes)} MB). " +
                $"Estimated peak requirement: {FormatMiB(check.EstimatedRequiredBytes)} MB (incl. headroom), " +
                $"available RAM: {FormatMiB(check.AvailableSystemBytes)} MB.");
        }

        AppLogger.Info($"ONNX memory guard passed: {check.DiagnosticMessage}");
    }

    static long ResolveTotalModelSizeBytes(string modelPath, long primaryModelBytes) {
        long total = Math.Max(0L, primaryModelBytes);
        try {
            string[] companionCandidates = [
                modelPath + ".data",
                modelPath + "_data"
            ];
            for (int i = 0; i < companionCandidates.Length; i++) {
                string candidate = companionCandidates[i];
                if (File.Exists(candidate)) {
                    long extra = new FileInfo(candidate).Length;
                    if (extra > 0) {
                        total = total > long.MaxValue - extra ? long.MaxValue : total + extra;
                    }
                }
            }
        } catch {
            // Best-effort companion external-data inspection
        }
        return total;
    }

    static long EstimateRequiredModelBytes(long modelBytes) {
        if (modelBytes <= 0) {
            return SessionBaseOverheadBytes;
        }
        double expanded = modelBytes * ModelMemoryExpansionFactor;
        if (!double.IsFinite(expanded) || expanded >= long.MaxValue - SessionBaseOverheadBytes) {
            return long.MaxValue;
        }
        return (long)Math.Ceiling(expanded) + SessionBaseOverheadBytes;
    }

    static long ComputeEffectiveSafetyHeadroom(long totalSystemBytes, long lowMemoryThresholdBytes) {
        long cappedThreshold = lowMemoryThresholdBytes > 0 ? lowMemoryThresholdBytes : 0L;
        if (totalSystemBytes > 0) {
            long quarterRam = totalSystemBytes / 4L;
            if (cappedThreshold > quarterRam) {
                cappedThreshold = quarterRam;
            }
        }
        return Math.Max(MinimumSafetyHeadroomBytes, cappedThreshold);
    }

    bool TryReadSystemMemory(
        out long availableBytes,
        out long totalBytes,
        out long thresholdBytes,
        out bool isLowMemory,
        out string source) {
        availableBytes = 0L;
        totalBytes = 0L;
        thresholdBytes = 0L;
        isLowMemory = false;
        source = "Unavailable";

        try {
            if (appContext.GetSystemService(Context.ActivityService) is Android.App.ActivityManager activityManager) {
                using Android.App.ActivityManager.MemoryInfo memInfo = new();
                activityManager.GetMemoryInfo(memInfo);
                if (memInfo.AvailMem > 0) {
                    availableBytes = memInfo.AvailMem;
                    totalBytes = memInfo.TotalMem > 0 ? memInfo.TotalMem : 0L;
                    thresholdBytes = memInfo.Threshold > 0 ? memInfo.Threshold : 0L;
                    isLowMemory = memInfo.LowMemory;
                    source = "ActivityManager";
                    return true;
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"ActivityManager.GetMemoryInfo failed, falling back to /proc/meminfo: {ex.Message}");
        }

        if (TryReadProcMemInfo(out long procAvail, out long procTotal)) {
            availableBytes = procAvail;
            totalBytes = procTotal;
            thresholdBytes = MinimumSafetyHeadroomBytes;
            isLowMemory = procAvail < MinimumSafetyHeadroomBytes;
            source = "/proc/meminfo";
            return true;
        }

        return false;
    }

    static bool TryReadProcMemInfo(out long availableBytes, out long totalBytes) {
        availableBytes = 0L;
        totalBytes = 0L;
        try {
            if (!File.Exists("/proc/meminfo")) {
                return false;
            }
            long memAvailableKib = -1L;
            long memFreeKib = 0L;
            long buffersKib = 0L;
            long cachedKib = 0L;
            long memTotalKib = 0L;

            foreach (string line in File.ReadLines("/proc/meminfo")) {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) {
                    memTotalKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) {
                    memAvailableKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("MemFree:", StringComparison.Ordinal)) {
                    memFreeKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("Buffers:", StringComparison.Ordinal)) {
                    buffersKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("Cached:", StringComparison.Ordinal)) {
                    cachedKib = ParseMemInfoKib(line);
                }
            }

            long effectiveAvailKib = memAvailableKib >= 0L
                ? memAvailableKib
                : (memFreeKib + buffersKib + cachedKib);
            if (effectiveAvailKib <= 0L) {
                return false;
            }

            availableBytes = effectiveAvailKib * 1024L;
            totalBytes = memTotalKib > 0L ? memTotalKib * 1024L : 0L;
            return true;
        } catch {
            return false;
        }
    }

    static long ParseMemInfoKib(string line) {
        int colon = line.IndexOf(':');
        if (colon < 0) {
            return 0L;
        }
        int i = colon + 1;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) {
            i++;
        }
        int start = i;
        while (i < line.Length && char.IsDigit(line[i])) {
            i++;
        }
        if (i <= start) {
            return 0L;
        }
        return long.TryParse(line.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long kib)
            ? kib
            : 0L;
    }

    static string FormatMiB(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    void ReleaseSessionRef() {
        lock (initLock) {
            if (activeSessionCount > 0) {
                activeSessionCount--;
            }
            if (disposeRequested && activeSessionCount == 0) {
                CleanupPartialInitLocked();
            }
        }
    }

    List<NeuralExecutionProvider> BuildSessionProviderCascade(NeuralExecutionProvider preferred) {
        List<NeuralExecutionProvider> list = [];
        bool qnnReady = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                        OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        if ((preferred == NeuralExecutionProvider.Auto || preferred == NeuralExecutionProvider.QnnHtp) && qnnReady) {
            list.Add(NeuralExecutionProvider.QnnHtp);
        }
        if (preferred != NeuralExecutionProvider.Cpu) {
            list.Add(NeuralExecutionProvider.Xnnpack);
        }
        list.Add(NeuralExecutionProvider.Cpu);
        return list;
    }

    string ConfigureSingleExecutionProvider(IntPtr sessOpts, NeuralExecutionProvider provider, int threads) {
        if (fnAppendEp == null || provider == NeuralExecutionProvider.Cpu) {
            return "CPU";
        }
        if (provider == NeuralExecutionProvider.QnnHtp &&
            OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
            OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice()) {
            string qnnPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(appContext);
            if (TryAppendEp(sessOpts, "QNN",
                ["backend_path", "htp_performance_mode", "enable_htp_fp16_precision"],
                [qnnPath, "burst", "1"])) {
                return "QNN HTP (NPU)";
            }
            return "CPU";
        }
        if (provider == NeuralExecutionProvider.Xnnpack) {
            if (TryAppendEp(sessOpts, "XNNPACK", ["intra_op_num_threads"], [threads.ToString()])) {
                return "XNNPACK (ARM64 SIMD)";
            }
            return "CPU";
        }
        return "CPU";
    }

    string ConfigureExecutionProvider(IntPtr sessOpts, NeuralExecutionProvider preferred, int threads) {
        if (fnAppendEp == null) {
            return "CPU";
        }

        bool tryQnn = (preferred == NeuralExecutionProvider.QnnHtp || preferred == NeuralExecutionProvider.Auto) &&
                      OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                      OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        if (tryQnn) {
            string qnnPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(appContext);
            if (TryAppendEp(sessOpts, "QNN",
                ["backend_path", "htp_performance_mode", "enable_htp_fp16_precision"],
                [qnnPath, "burst", "1"])) {
                return "QNN HTP (NPU)";
            }
        }

        bool tryXnn = preferred == NeuralExecutionProvider.Xnnpack ||
                      preferred == NeuralExecutionProvider.Auto ||
                      preferred == NeuralExecutionProvider.QnnHtp;
        if (tryXnn) {
            if (TryAppendEp(sessOpts, "XNNPACK", ["intra_op_num_threads"], [threads.ToString()])) {
                return "XNNPACK (ARM64 SIMD)";
            }
        }

        return "CPU";
    }

    bool TryAppendEp(IntPtr sessOpts, string epName, string[] keys, string[] values) {
        IntPtr[] keyPtrs = new IntPtr[keys.Length];
        IntPtr[] valPtrs = new IntPtr[values.Length];
        try {
            for (int i = 0; i < keys.Length; i++) {
                keyPtrs[i] = Marshal.StringToCoTaskMemUTF8(keys[i]);
                valPtrs[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            }
            IntPtr st = fnAppendEp(sessOpts, epName, keyPtrs, valPtrs, (UIntPtr)keys.Length);
            if (st == IntPtr.Zero) {
                return true;
            }
            string msg = ReadAndReleaseStatus(st);
            AppLogger.Warn($"ONNX Runtime EP '{epName}' unavailable, falling back: {msg}");
            return false;
        } catch (Exception ex) {
            AppLogger.Warn($"ONNX Runtime EP '{epName}' threw: {ex.Message}");
            return false;
        } finally {
            for (int i = 0; i < keyPtrs.Length; i++) {
                if (keyPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(keyPtrs[i]);
            }
            for (int i = 0; i < valPtrs.Length; i++) {
                if (valPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(valPtrs[i]);
            }
        }
    }

    void PreloadCompanionLibrariesLocked(string runtimeDir) {
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir)) {
            return;
        }
        HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < PreferredCompanionLoadOrder.Length; i++) {
            string p = Path.Combine(runtimeDir, PreferredCompanionLoadOrder[i]);
            if (File.Exists(p) && loaded.Add(p)) {
                if (NativeLibrary.TryLoad(p, out IntPtr handle)) {
                    companionLibHandles.Add(handle);
                }
            }
        }
        try {
            foreach (string so in Directory.GetFiles(runtimeDir, "libQnn*.so")) {
                if (loaded.Add(so) && NativeLibrary.TryLoad(so, out IntPtr handle)) {
                    companionLibHandles.Add(handle);
                }
            }
        } catch { }
    }

    bool TryInitializeLocked() {
        if (initialized) {
            return true;
        }
        try {
            lastInitError = "";
            string runtimeDir = OnnxRuntimeInstallerService.GetRuntimeDir(appContext);
            string libPath = OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext);

            uint headerApiVersion = 17u;
            string parseDiag = "header asset is empty or missing";
            string headerText = OnnxRuntimeInstallerService.ReadHeaderText(appContext);
            if (!string.IsNullOrWhiteSpace(headerText)) {
                (headerApiVersion, apiSlots, parseDiag) = OnnxRuntimeHeaderParser.ParseOrtApiHeaderWithDiagnostics(headerText);
            }
            if (apiSlots.Count < 20) {
                lastInitError = $"ONNX Runtime C API header validation failed: {parseDiag}";
                AppLogger.Warn($"ONNX Runtime C API header missing from APK assets or failed anchor validation ({parseDiag}); cannot safely bind vtable.");
                return false;
            }

            PreloadCompanionLibrariesLocked(runtimeDir);
            if (!string.IsNullOrEmpty(libPath) && File.Exists(libPath)) {
                libHandle = NativeLibrary.Load(libPath);
            } else if (!NativeLibrary.TryLoad("onnxruntime", typeof(NativeOnnxRuntimeBridge).Assembly, null, out libHandle)) {
                lastInitError = "libonnxruntime.so could not be loaded from NativeLibraryDir";
                AppLogger.Warn($"ONNX Runtime native library failed to load: '{libPath}'");
                CleanupPartialInitLocked();
                return false;
            }
            IntPtr getBaseSym = NativeLibrary.GetExport(libHandle, "OrtGetApiBase");
            OrtGetApiBaseDelegate getBase = Marshal.GetDelegateForFunctionPointer<OrtGetApiBaseDelegate>(getBaseSym);
            IntPtr apiBasePtr = getBase();
            if (apiBasePtr == IntPtr.Zero) {
                lastInitError = "OrtGetApiBase returned null";
                CleanupPartialInitLocked();
                return false;
            }

            IntPtr getApiFnPtr = Marshal.ReadIntPtr(apiBasePtr, 0);
            IntPtr getVerFnPtr = Marshal.ReadIntPtr(apiBasePtr, IntPtr.Size);
            GetApiDelegate getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiFnPtr);
            GetVersionStringDelegate getVer = Marshal.GetDelegateForFunctionPointer<GetVersionStringDelegate>(getVerFnPtr);

            IntPtr verStrPtr = getVer();
            loadedVersion = verStrPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(verStrPtr) ?? "") : "";

            uint resolvedApiVersion = headerApiVersion;
            ortApiPtr = getApi(headerApiVersion);
            uint fallbackStart = headerApiVersion > 14u ? headerApiVersion - 1u : 31u;
            for (uint v = fallbackStart; ortApiPtr == IntPtr.Zero && v >= 14u; v--) {
                ortApiPtr = getApi(v);
                if (ortApiPtr != IntPtr.Zero) {
                    resolvedApiVersion = v;
                }
            }
            if (ortApiPtr == IntPtr.Zero) {
                lastInitError = $"OrtGetApiBase()->GetApi({headerApiVersion}) returned null";
                AppLogger.Warn(lastInitError);
                CleanupPartialInitLocked();
                return false;
            }
            if (resolvedApiVersion < headerApiVersion) {
                AppLogger.Warn($"ONNX Runtime native GetApi fell back from header apiVer={headerApiVersion} to {resolvedApiVersion} (lib v{loadedVersion}).");
            }

            BindApiDelegates();

            CheckStatus(fnCreateEnv(OrtLoggingLevelWarning, "CavernizeNeural", out ortEnv));
            CheckStatus(fnCreateCpuMemInfo(OrtArenaAllocator, OrtMemTypeDefault, out cpuMemoryInfo));
            CheckStatus(fnGetAllocator(out defaultAllocator));

            // Probe best available EP dynamically so GetRuntimeInfo() reports the real active backend
            if (fnCreateSessionOptions != null && fnReleaseSessionOptions != null) {
                IntPtr probeSt = fnCreateSessionOptions(out IntPtr probeOpts);
                if (probeSt == IntPtr.Zero && probeOpts != IntPtr.Zero) {
                    try {
                        lastActiveProvider = ConfigureExecutionProvider(probeOpts, NeuralExecutionProvider.Auto, 2);
                    } finally {
                        fnReleaseSessionOptions(probeOpts);
                    }
                } else {
                    ReleaseStatusQuietly(probeSt);
                }
            }

            initialized = true;
            lastInitError = "";
            AppLogger.Info($"Initialized ONNX Runtime native bridge v{loadedVersion} ({apiSlots.Count} OrtApi symbols, apiVer={resolvedApiVersion}, EP={lastActiveProvider})");
            return true;
        } catch (Exception ex) {
            lastInitError = ex.Message;
            AppLogger.Warn($"Failed to initialize ONNX Runtime native bridge: {ex.Message}");
            CleanupPartialInitLocked();
            return false;
        }
    }

    void BindApiDelegates() {
        fnCreateEnv = GetSlotDelegate<CreateEnvDelegate>("CreateEnv");
        fnCreateSessionOptions = GetSlotDelegate<CreateSessionOptionsDelegate>("CreateSessionOptions");
        fnSetIntraOpNumThreads = GetSlotDelegate<SetIntraOpNumThreadsDelegate>("SetIntraOpNumThreads");
        fnSetInterOpNumThreads = TryGetSlotDelegate<SetInterOpNumThreadsDelegate>("SetInterOpNumThreads");
        fnSetGraphOpt = GetSlotDelegate<SetGraphOptimizationLevelDelegate>("SetSessionGraphOptimizationLevel");
        fnAddSessionConfig = TryGetSlotDelegate<AddSessionConfigEntryDelegate>("AddSessionConfigEntry");
        fnAppendEp = TryGetSlotDelegate<SessionOptionsAppendEpDelegate>("SessionOptionsAppendExecutionProvider");
        fnCreateSession = GetSlotDelegate<CreateSessionDelegate>("CreateSession");
        fnGetInputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetInputCount");
        fnGetOutputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetOutputCount");
        fnGetInputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetInputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetInputNameAllocated");
        fnGetOutputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputNameAllocated");
        fnGetInputTypeInfo = TryGetSlotDelegate<SessionGetTypeInfoDelegate>("SessionGetInputTypeInfo");
        fnCastTypeInfoToTensorInfo = TryGetSlotDelegate<CastTypeInfoToTensorInfoDelegate>("CastTypeInfoToTensorInfo");
        fnGetAllocator = GetSlotDelegate<GetAllocatorWithDefaultOptionsDelegate>("GetAllocatorWithDefaultOptions");
        fnAllocatorFree = GetSlotDelegate<AllocatorFreeDelegate>("AllocatorFree");
        fnCreateCpuMemInfo = GetSlotDelegate<CreateCpuMemoryInfoDelegate>("CreateCpuMemoryInfo");
        fnCreateTensor = GetSlotDelegate<CreateTensorWithDataAsOrtValueDelegate>("CreateTensorWithDataAsOrtValue");
        fnRun = GetSlotDelegate<RunDelegate>("Run");
        fnGetTensorData = GetSlotDelegate<GetTensorMutableDataDelegate>("GetTensorMutableData");
        fnGetTensorTypeAndShape = GetSlotDelegate<GetTensorTypeAndShapeDelegate>("GetTensorTypeAndShape");
        fnGetTensorElementType = TryGetSlotDelegate<GetTensorElementTypeDelegate>("GetTensorElementType");
        fnGetDimsCount = GetSlotDelegate<GetDimensionsCountDelegate>("GetDimensionsCount");
        fnGetDims = GetSlotDelegate<GetDimensionsDelegate>("GetDimensions");
        fnGetErrorMessage = GetSlotDelegate<GetErrorMessageDelegate>("GetErrorMessage");
        fnReleaseStatus = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseStatus");
        fnReleaseEnv = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseEnv");
        fnReleaseSession = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseSession");
        fnReleaseSessionOptions = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseSessionOptions");
        fnReleaseValue = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseValue");
        fnReleaseMemoryInfo = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseMemoryInfo");
        fnReleaseTensorTypeAndShapeInfo = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseTensorTypeAndShapeInfo");
        fnReleaseTypeInfo = TryGetSlotDelegate<ReleaseHandleDelegate>("ReleaseTypeInfo");
    }

    T GetSlotDelegate<T>(string name) where T : Delegate {
        if (!apiSlots.TryGetValue(name, out int slot)) {
            throw new EntryPointNotFoundException($"OrtApi slot '{name}' was not found in onnxruntime_c_api.h.");
        }
        IntPtr fnPtr = Marshal.ReadIntPtr(ortApiPtr, slot * IntPtr.Size);
        if (fnPtr == IntPtr.Zero) {
            throw new EntryPointNotFoundException($"OrtApi function pointer for '{name}' (slot {slot}) is null.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    T TryGetSlotDelegate<T>(string name) where T : Delegate {
        if (!apiSlots.TryGetValue(name, out int slot)) {
            return null;
        }
        IntPtr fnPtr = Marshal.ReadIntPtr(ortApiPtr, slot * IntPtr.Size);
        return fnPtr != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(fnPtr) : null;
    }

    void CheckStatus(IntPtr status) {
        if (status == IntPtr.Zero) {
            return;
        }
        string message = ReadAndReleaseStatus(status);
        throw new InvalidOperationException($"ONNX Runtime error: {message}");
    }

    string ReadAndReleaseStatus(IntPtr status) {
        if (status == IntPtr.Zero) {
            return "";
        }
        try {
            IntPtr msgPtr = fnGetErrorMessage != null ? fnGetErrorMessage(status) : IntPtr.Zero;
            return msgPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(msgPtr) ?? "Unknown ORT error") : "Unknown ORT error";
        } finally {
            fnReleaseStatus?.Invoke(status);
        }
    }

    void ReleaseStatusQuietly(IntPtr status) {
        if (status != IntPtr.Zero) {
            fnReleaseStatus?.Invoke(status);
        }
    }

    void CleanupPartialInitLocked() {
        if (cpuMemoryInfo != IntPtr.Zero) {
            fnReleaseMemoryInfo?.Invoke(cpuMemoryInfo);
            cpuMemoryInfo = IntPtr.Zero;
        }
        if (ortEnv != IntPtr.Zero) {
            fnReleaseEnv?.Invoke(ortEnv);
            ortEnv = IntPtr.Zero;
        }
        defaultAllocator = IntPtr.Zero;

        // Clear all native function-pointer delegates before unloading libonnxruntime.so so no dangling pointers remain
        fnCreateEnv = null;
        fnCreateSessionOptions = null;
        fnSetIntraOpNumThreads = null;
        fnSetInterOpNumThreads = null;
        fnSetGraphOpt = null;
        fnAddSessionConfig = null;
        fnAppendEp = null;
        fnCreateSession = null;
        fnGetInputCount = null;
        fnGetOutputCount = null;
        fnGetInputName = null;
        fnGetOutputName = null;
        fnGetInputTypeInfo = null;
        fnCastTypeInfoToTensorInfo = null;
        fnGetAllocator = null;
        fnAllocatorFree = null;
        fnCreateCpuMemInfo = null;
        fnCreateTensor = null;
        fnRun = null;
        fnGetTensorData = null;
        fnGetTensorTypeAndShape = null;
        fnGetTensorElementType = null;
        fnGetDimsCount = null;
        fnGetDims = null;
        fnGetErrorMessage = null;
        fnReleaseStatus = null;
        fnReleaseEnv = null;
        fnReleaseSession = null;
        fnReleaseSessionOptions = null;
        fnReleaseValue = null;
        fnReleaseMemoryInfo = null;
        fnReleaseTensorTypeAndShapeInfo = null;
        fnReleaseTypeInfo = null;

        if (libHandle != IntPtr.Zero) {
            try { NativeLibrary.Free(libHandle); } catch { }
            libHandle = IntPtr.Zero;
        }
        for (int i = companionLibHandles.Count - 1; i >= 0; i--) {
            if (companionLibHandles[i] != IntPtr.Zero) {
                try { NativeLibrary.Free(companionLibHandles[i]); } catch { }
            }
        }
        companionLibHandles.Clear();
        apiSlots.Clear();
        ortApiPtr = IntPtr.Zero;
        loadedVersion = "";
        lastActiveProvider = "";
        initialized = false;
    }

    static long ComputeCheckedElementCount(long[] dims, bool allowZero) {
        if (dims == null || dims.Length == 0) {
            return 1L;
        }
        long total = 1L;
        for (int d = 0; d < dims.Length; d++) {
            long dim = dims[d];
            if (dim < 0) {
                throw new InvalidDataException($"ONNX tensor has negative dimension {dim} at axis {d}.");
            }
            if (dim == 0) {
                if (allowZero) {
                    return 0L;
                }
                throw new InvalidDataException($"ONNX input tensor has zero dimension at axis {d}.");
            }
            if (total > MaxTensorElements / dim) {
                throw new InvalidDataException($"ONNX tensor element count exceeds safe mobile limit ({MaxTensorElements} elements).");
            }
            total *= dim;
        }
        return total;
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (initLock) {
            disposeRequested = true;
            if (activeSessionCount == 0) {
                CleanupPartialInitLocked();
            }
        }
    }

    sealed class NativeOnnxSession : INeuralSession {
        readonly NativeOnnxRuntimeBridge bridge;
        readonly object sessionLock = new();
        IntPtr sessionHandle;
        readonly string[] inputNames;
        readonly string[] outputNames;
        readonly long[][] inputShapes;
        readonly Dictionary<string, int> inputElementTypes;
        bool disposed;

        public NativeOnnxSession(NativeOnnxRuntimeBridge bridge, string modelPath, IntPtr handle, string activeEp) {
            this.bridge = bridge;
            ModelPath = modelPath ?? string.Empty;
            sessionHandle = handle;
            ExecutionProvider = activeEp;
            try {
                inputNames = ReadNames(isInput: true);
                outputNames = ReadNames(isInput: false);
                (inputShapes, inputElementTypes) = ReadInputMetadata(inputNames);
            } catch {
                if (sessionHandle != IntPtr.Zero) {
                    bridge.fnReleaseSession?.Invoke(sessionHandle);
                    sessionHandle = IntPtr.Zero;
                }
                throw;
            }
        }

        public string ModelPath { get; }
        public string ExecutionProvider { get; }
        public IReadOnlyList<string> InputNames => inputNames;
        public IReadOnlyList<long[]> InputShapes => inputShapes;
        public IReadOnlyList<string> OutputNames => outputNames;

        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            ArgumentNullException.ThrowIfNull(inputTensor);
            if (inputNames.Length == 0) {
                throw new InvalidOperationException("ONNX model exposes no input tensors.");
            }
            Dictionary<string, NeuralTensorFloat> inputs = new(1, StringComparer.Ordinal) {
                [inputNames[0]] = inputTensor
            };
            IReadOnlyDictionary<string, NeuralTensorFloat> outputs = Run(inputs);
            NeuralTensorFloat[] ordered = new NeuralTensorFloat[outputNames.Length];
            for (int o = 0; o < outputNames.Length; o++) {
                outputs.TryGetValue(outputNames[o], out ordered[o]);
            }
            return ordered;
        }

        public IReadOnlyDictionary<string, NeuralTensorFloat> Run(IReadOnlyDictionary<string, NeuralTensorFloat> inputs) {
            ArgumentNullException.ThrowIfNull(inputs);
            lock (sessionLock) {
                if (disposed || sessionHandle == IntPtr.Zero) {
                    throw new ObjectDisposedException(nameof(NativeOnnxSession));
                }

                int inCount = inputs.Count;
                IntPtr[] inNamePtrs = new IntPtr[inCount];
                IntPtr[] inValPtrs = new IntPtr[inCount];
                GCHandle[] pinnedInputs = new GCHandle[inCount];

                int outCount = outputNames.Length;
                IntPtr[] outNamePtrs = new IntPtr[outCount];
                IntPtr[] outValPtrs = new IntPtr[outCount];

                try {
                    int idx = 0;
                    foreach (KeyValuePair<string, NeuralTensorFloat> kv in inputs) {
                        NeuralTensorFloat tensor = kv.Value ?? throw new ArgumentException($"Null input tensor '{kv.Key}'.", nameof(inputs));
                        long expectedElems = ComputeCheckedElementCount(tensor.Shape, allowZero: false);
                        if (tensor.Data.Length < expectedElems) {
                            throw new ArgumentException(
                                $"ONNX input tensor '{kv.Key}' buffer underflow: expected {expectedElems} elements for shape [{string.Join(",", tensor.Shape)}], got {tensor.Data.Length}.");
                        }
                        int elemCount = (int)expectedElems;

                        inNamePtrs[idx] = Marshal.StringToCoTaskMemUTF8(kv.Key);
                        int elemType = inputElementTypes.TryGetValue(kv.Key, out int expectedType)
                            ? expectedType
                            : OnnxTensorElementDataTypeFloat;

                        UIntPtr byteLen;
                        if (elemType == OnnxTensorElementDataTypeFloat16) {
                            ushort[] fp16 = new ushort[elemCount];
                            for (int k = 0; k < elemCount; k++) {
                                float v = tensor.Data[k];
                                if (!float.IsFinite(v)) {
                                    v = 0f;
                                } else if (v > Float16FiniteMax) {
                                    v = Float16FiniteMax;
                                } else if (v < -Float16FiniteMax) {
                                    v = -Float16FiniteMax;
                                }
                                fp16[k] = BitConverter.HalfToUInt16Bits((Half)v);
                            }
                            pinnedInputs[idx] = GCHandle.Alloc(fp16, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(ushort)));
                        } else if (elemType == OnnxTensorElementDataTypeDouble) {
                            double[] fp64 = new double[elemCount];
                            for (int k = 0; k < elemCount; k++) {
                                float v = tensor.Data[k];
                                fp64[k] = float.IsFinite(v) ? v : 0.0;
                            }
                            pinnedInputs[idx] = GCHandle.Alloc(fp64, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(double)));
                        } else if (elemType == OnnxTensorElementDataTypeFloat) {
                            pinnedInputs[idx] = GCHandle.Alloc(tensor.Data, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(float)));
                        } else {
                            throw new NotSupportedException($"ONNX input tensor '{kv.Key}' uses unsupported ONNXTensorElementDataType={elemType}.");
                        }

                        IntPtr dataPtr = pinnedInputs[idx].AddrOfPinnedObject();
                        bridge.CheckStatus(bridge.fnCreateTensor(
                            bridge.cpuMemoryInfo,
                            dataPtr,
                            byteLen,
                            tensor.Shape,
                            (UIntPtr)tensor.Shape.Length,
                            elemType,
                            out inValPtrs[idx]));
                        idx++;
                    }

                    for (int o = 0; o < outCount; o++) {
                        outNamePtrs[o] = Marshal.StringToCoTaskMemUTF8(outputNames[o]);
                    }

                    bridge.CheckStatus(bridge.fnRun(
                        sessionHandle,
                        IntPtr.Zero,
                        inNamePtrs,
                        inValPtrs,
                        (UIntPtr)inCount,
                        outNamePtrs,
                        (UIntPtr)outCount,
                        outValPtrs));

                    Dictionary<string, NeuralTensorFloat> results = new(outCount, StringComparer.Ordinal);
                    for (int o = 0; o < outCount; o++) {
                        IntPtr outVal = outValPtrs[o];
                        if (outVal == IntPtr.Zero) {
                            continue;
                        }
                        bridge.CheckStatus(bridge.fnGetTensorTypeAndShape(outVal, out IntPtr shapeInfo));
                        long[] dims;
                        int outElemType = OnnxTensorElementDataTypeFloat;
                        try {
                            if (bridge.fnGetTensorElementType != null) {
                                IntPtr typeSt = bridge.fnGetTensorElementType(shapeInfo, out int parsedType);
                                if (typeSt == IntPtr.Zero) {
                                    outElemType = parsedType;
                                } else {
                                    bridge.ReleaseStatusQuietly(typeSt);
                                }
                            }
                            bridge.CheckStatus(bridge.fnGetDimsCount(shapeInfo, out UIntPtr dimCount));
                            int dimCountInt = checked((int)dimCount);
                            if (dimCountInt < 0 || dimCountInt > 16) {
                                throw new InvalidDataException($"ONNX output tensor '{outputNames[o]}' has invalid rank {dimCountInt}.");
                            }
                            dims = new long[dimCountInt];
                            if (dims.Length > 0) {
                                bridge.CheckStatus(bridge.fnGetDims(shapeInfo, dims, dimCount));
                            }
                        } finally {
                            if (shapeInfo != IntPtr.Zero) {
                                bridge.fnReleaseTensorTypeAndShapeInfo?.Invoke(shapeInfo);
                            }
                        }

                        long checkedCount = ComputeCheckedElementCount(dims, allowZero: true);
                        int elemCount = checked((int)checkedCount);
                        float[] managed = elemCount > 0 ? new float[elemCount] : Array.Empty<float>();
                        if (elemCount > 0) {
                            bridge.CheckStatus(bridge.fnGetTensorData(outVal, out IntPtr rawPtr));
                            if (rawPtr == IntPtr.Zero) {
                                throw new InvalidDataException($"ONNX output tensor '{outputNames[o]}' returned a null native data pointer.");
                            }
                            if (outElemType == OnnxTensorElementDataTypeFloat16) {
                                short[] raw16 = new short[elemCount];
                                Marshal.Copy(rawPtr, raw16, 0, elemCount);
                                for (int k = 0; k < elemCount; k++) {
                                    float v = (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)raw16[k]));
                                    managed[k] = float.IsFinite(v) ? v : 0f;
                                }
                            } else if (outElemType == OnnxTensorElementDataTypeDouble) {
                                double[] raw64 = new double[elemCount];
                                Marshal.Copy(rawPtr, raw64, 0, elemCount);
                                for (int k = 0; k < elemCount; k++) {
                                    float v = (float)raw64[k];
                                    managed[k] = float.IsFinite(v) ? v : 0f;
                                }
                            } else if (outElemType == OnnxTensorElementDataTypeFloat) {
                                Marshal.Copy(rawPtr, managed, 0, elemCount);
                                for (int k = 0; k < elemCount; k++) {
                                    if (!float.IsFinite(managed[k])) {
                                        managed[k] = 0f;
                                    }
                                }
                            } else {
                                throw new NotSupportedException($"ONNX output tensor '{outputNames[o]}' uses unsupported ONNXTensorElementDataType={outElemType}.");
                            }
                        }
                        results[outputNames[o]] = new NeuralTensorFloat(dims, managed);
                    }

                    return results;
                } finally {
                    for (int i = 0; i < inCount; i++) {
                        if (inValPtrs[i] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(inValPtrs[i]);
                        if (pinnedInputs[i].IsAllocated) pinnedInputs[i].Free();
                        if (inNamePtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(inNamePtrs[i]);
                    }
                    for (int o = 0; o < outCount; o++) {
                        if (outValPtrs[o] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(outValPtrs[o]);
                        if (outNamePtrs[o] != IntPtr.Zero) Marshal.FreeCoTaskMem(outNamePtrs[o]);
                    }
                }
            }
        }

        string[] ReadNames(bool isInput) {
            SessionGetCountDelegate countFn = isInput ? bridge.fnGetInputCount : bridge.fnGetOutputCount;
            SessionGetNameDelegate nameFn = isInput ? bridge.fnGetInputName : bridge.fnGetOutputName;
            bridge.CheckStatus(countFn(sessionHandle, out UIntPtr count));
            int n = checked((int)count);
            if (n < 0 || n > 256) {
                throw new InvalidDataException($"ONNX model reported an invalid tensor count ({n}).");
            }
            string[] names = new string[n];
            for (int i = 0; i < n; i++) {
                bridge.CheckStatus(nameFn(sessionHandle, (UIntPtr)i, bridge.defaultAllocator, out IntPtr namePtr));
                try {
                    names[i] = namePtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(namePtr) ?? $"tensor_{i}") : $"tensor_{i}";
                } finally {
                    if (namePtr != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(bridge.fnAllocatorFree(bridge.defaultAllocator, namePtr));
                    }
                }
            }
            return names;
        }

        (long[][] Shapes, Dictionary<string, int> ElementTypes) ReadInputMetadata(string[] names) {
            int count = names.Length;
            long[][] shapes = new long[count][];
            Dictionary<string, int> types = new(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++) {
                shapes[i] = Array.Empty<long>();
                types[names[i]] = OnnxTensorElementDataTypeFloat;
            }
            if (bridge.fnGetInputTypeInfo == null || bridge.fnCastTypeInfoToTensorInfo == null) {
                return (shapes, types);
            }
            for (int i = 0; i < count; i++) {
                IntPtr typeInfo = IntPtr.Zero;
                try {
                    IntPtr st = bridge.fnGetInputTypeInfo(sessionHandle, (UIntPtr)i, out typeInfo);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    if (typeInfo == IntPtr.Zero) {
                        continue;
                    }
                    st = bridge.fnCastTypeInfoToTensorInfo(typeInfo, out IntPtr tensorInfo);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    if (tensorInfo == IntPtr.Zero) {
                        continue;
                    }
                    if (bridge.fnGetTensorElementType != null) {
                        IntPtr typeSt = bridge.fnGetTensorElementType(tensorInfo, out int elemType);
                        if (typeSt == IntPtr.Zero && elemType > 0) {
                            types[names[i]] = elemType;
                        } else {
                            bridge.ReleaseStatusQuietly(typeSt);
                        }
                    }
                    st = bridge.fnGetDimsCount(tensorInfo, out UIntPtr dimCount);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    int dimCountInt = checked((int)dimCount);
                    if (dimCountInt < 0 || dimCountInt > 16) {
                        continue;
                    }
                    long[] dims = new long[dimCountInt];
                    if (dims.Length > 0) {
                        st = bridge.fnGetDims(tensorInfo, dims, dimCount);
                        if (st != IntPtr.Zero) {
                            bridge.ReleaseStatusQuietly(st);
                            continue;
                        }
                    }
                    shapes[i] = dims;
                } catch {
                    // Leave empty fallback shape if runtime fails to inspect this slot
                } finally {
                    if (typeInfo != IntPtr.Zero) {
                        bridge.fnReleaseTypeInfo?.Invoke(typeInfo);
                    }
                }
            }
            return (shapes, types);
        }

        public void Dispose() {
            bool shouldReleaseRef = false;
            lock (sessionLock) {
                if (!disposed) {
                    disposed = true;
                    shouldReleaseRef = true;
                    if (sessionHandle != IntPtr.Zero) {
                        bridge.fnReleaseSession?.Invoke(sessionHandle);
                        sessionHandle = IntPtr.Zero;
                    }
                }
            }
            if (shouldReleaseRef) {
                bridge.ReleaseSessionRef();
            }
        }
    }
}

/// <summary>
/// Result of comparing available Android system RAM against the estimated peak native memory
/// footprint required to load an ONNX model checkpoint safely.
/// </summary>
public readonly struct ModelMemoryCheckResult {
    /// <summary>True when available system RAM meets the estimated model load + safety headroom budget.</summary>
    public bool HasSufficientMemory { get; init; }

    /// <summary>True when Android reports the device is currently in a low-memory state.</summary>
    public bool IsLowMemoryState { get; init; }

    /// <summary>Combined size on disk of the ONNX checkpoint and any external tensor data files, in bytes.</summary>
    public long ModelFileBytes { get; init; }

    /// <summary>Estimated peak native RAM needed to load the model plus OS/audio headroom, in bytes.</summary>
    public long EstimatedRequiredBytes { get; init; }

    /// <summary>Available system RAM reported by Android at check time, in bytes.</summary>
    public long AvailableSystemBytes { get; init; }

    /// <summary>Total physical system RAM accessible to the kernel, in bytes.</summary>
    public long TotalSystemBytes { get; init; }

    /// <summary>Android low-memory threshold reported by ActivityManager, in bytes.</summary>
    public long LowMemoryThresholdBytes { get; init; }

    /// <summary>Effective safety headroom reserved for the OS and real-time audio engine, in bytes.</summary>
    public long SafetyHeadroomBytes { get; init; }

    /// <summary>Telemetry backend used ("ActivityManager", "/proc/meminfo", or "Unavailable").</summary>
    public string Source { get; init; }

    /// <summary>Human-readable summary suitable for device logs and error reporting.</summary>
    public string DiagnosticMessage { get; init; }
}
